#!/usr/bin/env bash
set -Eeuo pipefail

echo "========================================"
echo "ECS Express deployment"
echo "========================================"

: "${AWS_REGION:?AWS_REGION is required}"
: "${ECS_CLUSTER:?ECS_CLUSTER is required}"
: "${ECS_SERVICE:?ECS_SERVICE is required}"
: "${ECS_CONTAINER_NAME:?ECS_CONTAINER_NAME is required}"
: "${IMAGE_URI:?IMAGE_URI is required}"

readonly HEALTH_CHECK_PATH="${HEALTH_CHECK_PATH:-/hello}"
readonly HEALTH_CHECK_INTERVAL_SECONDS="${HEALTH_CHECK_INTERVAL_SECONDS:-5}"

previous_task_definition=""
service_updated=false
new_task_definition=""

rollback() {
    local status=$?

    if [[ "${service_updated}" == "true" && -n "${previous_task_definition}" ]]; then
        echo ""
        echo "Deployment failed. Requesting ECS rollback..."

        aws ecs update-service \
            --region "${AWS_REGION}" \
            --cluster "${ECS_CLUSTER}" \
            --service "${ECS_SERVICE}" \
            --task-definition "${previous_task_definition}" \
            >/dev/null 2>&1 || true
    fi

    exit "${status}"
}

trap rollback ERR

echo "Region:       ${AWS_REGION}"
echo "Cluster:      ${ECS_CLUSTER}"
echo "Service:      ${ECS_SERVICE}"
echo "Container:    ${ECS_CONTAINER_NAME}"
echo "Image:        ${IMAGE_URI}"
echo "========================================"

#
# 1. Verify ECR image
#
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

#
# 2. Discover ECS service
#
echo ""
echo "2/7 Discovering ECS service..."

service_json="$(
    aws ecs describe-services \
        --region "${AWS_REGION}" \
        --cluster "${ECS_CLUSTER}" \
        --services "${ECS_SERVICE}" \
        --output json
)"

service_count="$(
    echo "${service_json}" |
        jq '.services | length'
)"

if [[ "${service_count}" -ne 1 ]]; then
    echo "ERROR: ECS service was not found."
    echo "Cluster: ${ECS_CLUSTER}"
    echo "Service: ${ECS_SERVICE}"
    exit 1
fi

service_status="$(
    echo "${service_json}" |
        jq -r '.services[0].status'
)"

if [[ "${service_status}" != "ACTIVE" ]]; then
    echo "ERROR: ECS service is not ACTIVE."
    echo "Status: ${service_status}"
    exit 1
fi

ECS_SERVICE_ARN="$(
    echo "${service_json}" |
        jq -r '.services[0].serviceArn'
)"

previous_task_definition="$(
    echo "${service_json}" |
        jq -r '.services[0].taskDefinition'
)"

echo "ECS service found."
echo "Service ARN: ${ECS_SERVICE_ARN}"
echo "Current task definition:"
echo "${previous_task_definition}"

#
# 3. Read current task definition
#
echo ""
echo "3/7 Reading current task definition..."

aws ecs describe-task-definition \
    --region "${AWS_REGION}" \
    --task-definition "${previous_task_definition}" \
    --query taskDefinition \
    --output json > task-definition.json

echo "Checking container '${ECS_CONTAINER_NAME}'..."

container_exists="$(
    jq \
        --arg container_name "${ECS_CONTAINER_NAME}" \
        '[.containerDefinitions[] | select(.name == $container_name)] | length' \
        task-definition.json
)"

if [[ "${container_exists}" -ne 1 ]]; then
    echo "ERROR: Container '${ECS_CONTAINER_NAME}' was not found."
    echo "Available containers:"
    jq -r '.containerDefinitions[].name' task-definition.json
    exit 1
fi

echo "Container found."

#
# ECS Express task definitions should not be registered
# with server-managed fields.
#
jq \
    --arg container_name "${ECS_CONTAINER_NAME}" \
    --arg image "${IMAGE_URI}" '
    if any(.containerDefinitions[]; .name == $container_name) then
        .containerDefinitions |=
        map(
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
    ' \
    task-definition.json > task-definition-register.json

echo "Task definition is compatible with ECS Express Mode."

#
# 4. Register new immutable task definition
#
echo ""
echo "4/7 Creating new immutable task-definition revision..."

new_task_definition="$(
    aws ecs register-task-definition \
        --region "${AWS_REGION}" \
        --cli-input-json file://task-definition-register.json \
        --query 'taskDefinition.taskDefinitionArn' \
        --output text
)"

if [[ -z "${new_task_definition}" || "${new_task_definition}" == "None" ]]; then
    echo "ERROR: Failed to create task-definition revision."
    exit 1
fi

echo "New task definition:"
echo "${new_task_definition}"

#
# 5. Update ECS Express service
#
echo ""
echo "5/7 Updating ECS Express service..."

update_response="$(
    aws ecs update-service \
        --region "${AWS_REGION}" \
        --cluster "${ECS_CLUSTER}" \
        --service "${ECS_SERVICE}" \
        --task-definition "${new_task_definition}" \
        --force-new-deployment \
        --output json
)"

target_service_revision="$(
    echo "${update_response}" |
        jq -r '.service.serviceRevisionArn // empty'
)"

if [[ -z "${target_service_revision}" ]]; then
    echo "ERROR: ECS did not return a target service revision."
    exit 1
fi

service_updated=true

echo "Express service update accepted."
echo "Target service revision:"
echo "${target_service_revision}"

#
# 6. Wait for the EXACT deployment created by this update.
#
echo ""
echo "6/7 Waiting for ECS Express deployment..."

deployment_arn=""

while [[ -z "${deployment_arn}" ]]; do

    deployment_json="$(
        aws ecs list-service-deployments \
            --region "${AWS_REGION}" \
            --service "${ECS_SERVICE_ARN}" \
            --max-results 20 \
            --output json
    )"

    deployment_arn="$(
        echo "${deployment_json}" |
            jq -r \
                --arg revision "${target_service_revision}" '
                    .serviceDeployments[]
                    | select(.targetServiceRevisionArn == $revision)
                    | .serviceDeploymentArn
                ' |
            head -n 1
    )"

    if [[ -z "${deployment_arn}" ]]; then
        echo "Deployment record is not available yet. Waiting..."
        sleep "${HEALTH_CHECK_INTERVAL_SECONDS}"
    fi
done

echo "Deployment record found:"
echo "${deployment_arn}"

#
# Continue until ECS reaches a terminal deployment state.
#
while true; do

    deployment_details="$(
        aws ecs describe-service-deployments \
            --region "${AWS_REGION}" \
            --service-deployment-arns "${deployment_arn}" \
            --output json
    )"

    deployment_status="$(
        echo "${deployment_details}" |
            jq -r '.serviceDeployments[0].status'
    )"

    deployment_reason="$(
        echo "${deployment_details}" |
            jq -r '.serviceDeployments[0].statusReason // ""'
    )"

    echo "ECS Express deployment status: ${deployment_status}"

    case "${deployment_status}" in

        SUCCESSFUL)
            echo ""
            echo "ECS Express deployment completed successfully."
            break
            ;;

        PENDING|IN_PROGRESS|ROLLBACK_REQUESTED|ROLLBACK_IN_PROGRESS)
            sleep "${HEALTH_CHECK_INTERVAL_SECONDS}"
            ;;

        ROLLBACK_SUCCESSFUL)
            echo ""
            echo "ECS Express automatically rolled back the deployment."
            echo "Reason: ${deployment_reason}"
            exit 1
            ;;

        ROLLBACK_FAILED)
            echo ""
            echo "ECS Express rollback FAILED."
            echo "Reason: ${deployment_reason}"
            exit 1
            ;;

        STOPPED|STOP_REQUESTED)
            echo ""
            echo "ECS Express deployment stopped."
            echo "Reason: ${deployment_reason}"
            exit 1
            ;;

        *)
            echo ""
            echo "Unknown ECS deployment status: ${deployment_status}"
            echo "Reason: ${deployment_reason}"
            exit 1
            ;;
    esac

done

#
# 7. Application health check
#
echo ""
echo "7/7 Verifying application health..."

if [[ -n "${HEALTH_CHECK_URL:-}" ]]; then

    health_url="${HEALTH_CHECK_URL%/}${HEALTH_CHECK_PATH}"

    echo "Health check URL:"
    echo "${health_url}"

    while true; do

        status="$(
            curl \
                --silent \
                --output /dev/null \
                --write-out '%{http_code}' \
                --max-time 10 \
                "${health_url}" || true
        )"

        echo "Health check returned HTTP ${status}"

        if [[ "${status}" == "200" ]]; then
            echo ""
            echo "Application health check passed."

            printf 'task_definition=%s\n' \
                "${new_task_definition}" \
                >> "${GITHUB_OUTPUT:-/dev/stdout}"

            echo ""
            echo "========================================"
            echo "DEPLOYMENT SUCCESSFUL"
            echo "========================================"

            exit 0
        fi

        sleep "${HEALTH_CHECK_INTERVAL_SECONDS}"
    done

else

    echo "HEALTH_CHECK_URL is not configured."
    echo "ECS Express deployment completed successfully."

    printf 'task_definition=%s\n' \
        "${new_task_definition}" \
        >> "${GITHUB_OUTPUT:-/dev/stdout}"

    exit 0
fi
