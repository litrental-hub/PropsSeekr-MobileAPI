#!/usr/bin/env bash

# Deploys one immutable ECR image to an Amazon ECS Express Mode service.
#
# Flow:
# 1. Verify the image exists in ECR.
# 2. Discover the current Express service and current task definition.
# 3. Create a new immutable ECS task-definition revision with the new image.
# 4. Update the ECS Express service to that task definition.
# 5. Wait for the specific Express service deployment to finish.
# 6. Verify the public application health endpoint.
# 7. If the deployment/health check fails, restore the previous task definition.
#
# ECS Express Mode uses update-express-gateway-service rather than update-service.

set -euo pipefail

: "${AWS_REGION:?AWS_REGION is required}"
: "${ECS_CLUSTER:?ECS_CLUSTER is required}"
: "${ECS_SERVICE:?ECS_SERVICE is required}"
: "${ECS_CONTAINER_NAME:?ECS_CONTAINER_NAME is required}"
: "${IMAGE_URI:?IMAGE_URI is required}"

readonly HEALTH_CHECK_PATH="${HEALTH_CHECK_PATH:-/hello}"
readonly HEALTH_CHECK_ATTEMPTS="${HEALTH_CHECK_ATTEMPTS:-30}"
readonly HEALTH_CHECK_INTERVAL_SECONDS="${HEALTH_CHECK_INTERVAL_SECONDS:-10}"
readonly DEPLOYMENT_ATTEMPTS="${DEPLOYMENT_ATTEMPTS:-120}"
readonly DEPLOYMENT_INTERVAL_SECONDS="${DEPLOYMENT_INTERVAL_SECONDS:-10}"

service_updated=false
previous_task_definition=""
new_task_definition=""
target_service_revision=""
deployment_arn=""

readonly SERVICE_ARN="arn:aws:ecs:${AWS_REGION}:${AWS_ACCOUNT_ID}:service/${ECS_CLUSTER}/${ECS_SERVICE}"

echo "========================================"
echo "ECS Express deployment"
echo "========================================"
echo "Region:       ${AWS_REGION}"
echo "Cluster:      ${ECS_CLUSTER}"
echo "Service:      ${ECS_SERVICE}"
echo "Container:    ${ECS_CONTAINER_NAME}"
echo "Service ARN:  ${SERVICE_ARN}"
echo "Image:        ${IMAGE_URI}"
echo "========================================"

rollback() {
  local status=$?

  if [[ "${service_updated}" == true && -n "${previous_task_definition}" ]]; then
    echo ""
    echo "Deployment failed."
    echo "Previous task definition:"
    echo "${previous_task_definition}"

    # Check whether ECS Express already rolled back automatically.
    local current_task_definition=""

    current_task_definition="$(
      aws ecs describe-express-gateway-service \
        --region "${AWS_REGION}" \
        --service-arn "${SERVICE_ARN}" \
        --query 'service.activeConfigurations[0].taskDefinitionArn' \
        --output text 2>/dev/null || true
    )"

    if [[ "${current_task_definition}" == "${previous_task_definition}" ]]; then
      echo "ECS Express has already returned to the previous task definition."
    else
      echo "Restoring previous task definition..."

      aws ecs update-express-gateway-service \
        --region "${AWS_REGION}" \
        --service-arn "${SERVICE_ARN}" \
        --task-definition-arn "${previous_task_definition}" \
        >/dev/null || true

      echo "Rollback request submitted."
    fi
  fi

  exit "${status}"
}

trap rollback ERR

echo ""
echo "1/7 Verifying ECR image..."

image_tag="${IMAGE_URI##*:}"

repository_name="${IMAGE_URI#*/}"
repository_name="${repository_name%%:*}"

aws ecr describe-images \
  --region "${AWS_REGION}" \
  --repository-name "${repository_name}" \
  --image-ids "imageTag=${image_tag}" \
  >/dev/null

echo "ECR image exists."

echo ""
echo "2/7 Discovering ECS Express service..."

service_json="$(
  aws ecs describe-express-gateway-service \
    --region "${AWS_REGION}" \
    --service-arn "${SERVICE_ARN}" \
    --output json
)"

service_status="$(jq -r '.service.status.statusCode' <<< "${service_json}")"

if [[ "${service_status}" != "ACTIVE" ]]; then
  echo "ECS Express service is not ACTIVE: ${service_status}" >&2
  exit 1
fi

actual_cluster="$(jq -r '.service.cluster' <<< "${service_json}")"
actual_service="$(jq -r '.service.serviceName' <<< "${service_json}")"
actual_cluster_name="${actual_cluster##*/}"

if [[ "${actual_cluster}" != "${ECS_CLUSTER}" && "${actual_cluster_name}" != "${ECS_CLUSTER}" ]]; then
  echo "Cluster mismatch." >&2
  echo "Expected: ${ECS_CLUSTER}" >&2
  echo "Actual:   ${actual_cluster} (${actual_cluster_name})" >&2
  exit 1
fi

if [[ "${actual_service}" != "${ECS_SERVICE}" ]]; then
  echo "Service mismatch." >&2
  echo "Expected: ${ECS_SERVICE}" >&2
  echo "Actual:   ${actual_service}" >&2
  exit 1
fi

previous_task_definition="$(
  jq -r '.service.activeConfigurations[0].taskDefinitionArn // empty' \
    <<< "${service_json}"
)"

if [[ -z "${previous_task_definition}" || "${previous_task_definition}" == "null" ]]; then
  echo "Could not determine the current Express task definition." >&2
  exit 1
fi

echo "Express service found."
echo "Current task definition:"
echo "${previous_task_definition}"

echo ""
echo "3/7 Reading current task definition..."

aws ecs describe-task-definition \
  --region "${AWS_REGION}" \
  --task-definition "${previous_task_definition}" \
  --query 'taskDefinition' \
  --output json > task-definition.json

echo "Checking container '${ECS_CONTAINER_NAME}'..."

if ! jq -e \
  --arg container_name "${ECS_CONTAINER_NAME}" \
  '
    any(
      .containerDefinitions[];
      .name == $container_name
    )
  ' task-definition.json >/dev/null; then
  echo "Container not found in current task definition: ${ECS_CONTAINER_NAME}" >&2
  exit 1
fi

echo "Container found."

# ECS Express Mode custom task definitions require:
# - a container named Main
# - a TCP port mapping
# - Fargate compatibility
#
# Your existing container is named Main, but we validate the task definition
# so a bad revision fails before the service is updated.
if ! jq -e \
  --arg container_name "${ECS_CONTAINER_NAME}" \
  '
    (.requiresCompatibilities // []) | index("FARGATE")
  ' task-definition.json >/dev/null; then
  echo "Task definition does not contain FARGATE compatibility." >&2
  exit 1
fi

if ! jq -e \
  --arg container_name "${ECS_CONTAINER_NAME}" \
  '
    any(
      .containerDefinitions[];
      .name == $container_name
      and any(
        (.portMappings // [])[];
        (.containerPort != null)
        and ((.protocol // "tcp") == "tcp")
        and (.name != null)
      )
    )
  ' task-definition.json >/dev/null; then
  echo "The container does not have a valid named TCP port mapping required by ECS Express Mode." >&2
  exit 1
fi

echo "Task definition is compatible with ECS Express Mode."

echo ""
echo "4/7 Creating new immutable task-definition revision..."

jq \
  --arg container_name "${ECS_CONTAINER_NAME}" \
  --arg image "${IMAGE_URI}" \
  '
    if any(.containerDefinitions[]; .name == $container_name) then
      .containerDefinitions |= map(
        if .name == $container_name
        then .image = $image
        else .
        end
      )
    else
      error("Container not found in task definition: " + $container_name)
    end
    |
    del(
      .taskDefinitionArn,
      .revision,
      .status,
      .requiresAttributes,
      .compatibilities,
      .registeredAt,
      .registeredBy,
      .deregisteredAt
    )
  ' task-definition.json > task-definition-register.json

new_task_definition="$(
  aws ecs register-task-definition \
    --region "${AWS_REGION}" \
    --cli-input-json file://task-definition-register.json \
    --query 'taskDefinition.taskDefinitionArn' \
    --output text
)"

echo "New task definition:"
echo "${new_task_definition}"

echo ""
echo "5/7 Updating ECS Express service..."

target_service_revision="$(
  aws ecs update-express-gateway-service \
    --region "${AWS_REGION}" \
    --service-arn "${SERVICE_ARN}" \
    --task-definition-arn "${new_task_definition}" \
    --query 'service.targetConfiguration.serviceRevisionArn' \
    --output text
)"

if [[ -z "${target_service_revision}" || "${target_service_revision}" == "None" ]]; then
  echo "ECS Express did not return a target service revision." >&2
  exit 1
fi

service_updated=true

echo "Express service update accepted."
echo "Target service revision:"
echo "${target_service_revision}"

echo ""
echo "6/7 Waiting for ECS Express deployment..."

for ((attempt = 1; attempt <= DEPLOYMENT_ATTEMPTS; attempt++)); do

  deployment_json="$(
    aws ecs list-service-deployments \
      --region "${AWS_REGION}" \
      --service "${SERVICE_ARN}" \
      --max-results 20 \
      --output json
  )"

  deployment_arn="$(
    jq -r \
      --arg revision "${target_service_revision}" \
      '
        .serviceDeployments[]
        | select(.targetServiceRevisionArn == $revision)
        | .serviceDeploymentArn
      ' <<< "${deployment_json}" \
      | head -n 1
  )"

  if [[ -n "${deployment_arn}" ]]; then

    deployment_status="$(
      jq -r \
        --arg revision "${target_service_revision}" \
        '
          .serviceDeployments[]
          | select(.targetServiceRevisionArn == $revision)
          | .status
        ' <<< "${deployment_json}" \
        | head -n 1
    )"

    deployment_reason="$(
      jq -r \
        --arg revision "${target_service_revision}" \
        '
          .serviceDeployments[]
          | select(.targetServiceRevisionArn == $revision)
          | (.statusReason // "")
        ' <<< "${deployment_json}" \
        | head -n 1
    )"

    echo "Deployment ${attempt}/${DEPLOYMENT_ATTEMPTS}: ${deployment_status}"

    case "${deployment_status}" in

      SUCCESSFUL)
        echo "ECS Express deployment completed successfully."
        break
        ;;

      ROLLBACK_SUCCESSFUL)
        echo "ECS Express automatically rolled back the deployment." >&2
        echo "Reason: ${deployment_reason}" >&2
        exit 1
        ;;

      ROLLBACK_FAILED)
        echo "ECS Express rollback FAILED." >&2
        echo "Reason: ${deployment_reason}" >&2
        exit 1
        ;;

      STOPPED|STOP_REQUESTED)
        echo "ECS Express deployment stopped." >&2
        echo "Reason: ${deployment_reason}" >&2
        exit 1
        ;;

      PENDING|IN_PROGRESS|ROLLBACK_REQUESTED|ROLLBACK_IN_PROGRESS)
        ;;

      *)
        echo "Unknown ECS deployment status: ${deployment_status}" >&2
        echo "Reason: ${deployment_reason}" >&2
        exit 1
        ;;
    esac

  else
    echo "Waiting for deployment record... ${attempt}/${DEPLOYMENT_ATTEMPTS}"
  fi

  if [[ "${attempt}" -lt "${DEPLOYMENT_ATTEMPTS}" ]]; then
    sleep "${DEPLOYMENT_INTERVAL_SECONDS}"
  fi
done

if [[ "${deployment_status:-}" != "SUCCESSFUL" ]]; then
  echo "Timed out waiting for ECS Express deployment." >&2
  exit 1
fi

echo ""
echo "Running application health check..."

if [[ -n "${HEALTH_CHECK_URL:-}" ]]; then

  health_check_passed=false

  for ((attempt = 1; attempt <= HEALTH_CHECK_ATTEMPTS; attempt++)); do

    health_url="${HEALTH_CHECK_URL%/}${HEALTH_CHECK_PATH}"

    status="$(
      curl \
        --silent \
        --show-error \
        --output /dev/null \
        --write-out '%{http_code}' \
        --max-time 10 \
        "${health_url}" || true
    )"

    echo "Health check ${attempt}/${HEALTH_CHECK_ATTEMPTS}: HTTP ${status}"

    if [[ "${status}" == "200" ]]; then
      health_check_passed=true
      break
    fi

    if [[ "${attempt}" -lt "${HEALTH_CHECK_ATTEMPTS}" ]]; then
      sleep "${HEALTH_CHECK_INTERVAL_SECONDS}"
    fi
  done

  if [[ "${health_check_passed}" != true ]]; then
    echo "Application health check failed." >&2
    exit 1
  fi

  echo ""
  echo "Application health check passed."

else
  echo "HEALTH_CHECK_URL is not configured."
  echo "ECS Express deployment was successful, but external health verification was skipped." >&2
fi

echo ""
echo "========================================"
echo "DEPLOYMENT SUCCESSFUL"
echo "========================================"
echo "Previous task definition:"
echo "${previous_task_definition}"
echo ""
echo "New task definition:"
echo "${new_task_definition}"
echo ""
echo "Target service revision:"
echo "${target_service_revision}"
echo "========================================"

printf 'task_definition=%s\n' \
  "${new_task_definition}" >> "${GITHUB_OUTPUT:-/dev/stdout}"
