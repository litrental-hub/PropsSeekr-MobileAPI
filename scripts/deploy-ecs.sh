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

service_updated=false
previous_task_definition=""
new_task_definition=""
target_service_revision=""
deployment_arn=""

task_definition_file="task-definition.json"
register_file="task-definition-register.json"

rollback() {
    local status=$?

    if [[ "${service_updated}" == "true" && -n "${previous_task_definition}" ]]; then
        echo ""
        echo "Deployment failed."
        echo "Previous task definition:"
        echo "${previous_task_definition}"
        echo "Requesting ECS Express rollback..."

        aws ecs update-express-gateway-service \
            --region "${AWS_REGION}" \
            --service-arn "${ECS_SERVICE_ARN}" \
            --task-definition-arn "${previous_task_definition}" \
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
# 1. Verify AWS identity
#
echo ""
echo "1/7 Verifying AWS identity..."

aws sts get-caller-identity >/dev/null

echo "AWS credentials are valid."

#
# 2. Verify ECR image
#
echo ""
echo "2/7 Verifying ECR image..."

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
# 3. Discover ECS Express service
#
echo ""
echo "3/7 Discovering ECS Express service..."

service_json="$(
    aws ecs describe-services \
        --region "${AWS_REGION}" \
        --cluster "${ECS_CLUSTER}" \
        --services "${ECS_SERVICE}" \
        --output json
)"

service_count="$(
    jq '.services | length' <<< "${service_json}"
)"

if [[ "${service_count}" -ne 1 ]]; then
    echo "ERROR: ECS service was not found."
    echo "Cluster: ${ECS_CLUSTER}"
    echo "Service: ${ECS_SERVICE}"
    exit 1
fi

service_status="$(
    jq -r '.services[0].status' <<< "${service_json}"
)"

if [[ "${service_status}" != "ACTIVE" ]]; then
    echo "ERROR: ECS service is not ACTIVE."
    echo "Status: ${service_status}"
    exit 1
fi

ECS_SERVICE_ARN="$(
    jq -r '.services[0].serviceArn' <<< "${service_json}"
)"

echo "Express service found."
echo "Service ARN:"
echo "${ECS_SERVICE_ARN}"

#
# IMPORTANT:
# ECS Express Mode does NOT expose the active task definition
# through DescribeServices.
#
# Get it from DescribeExpressGatewayService.
#
echo ""
echo "Reading current ECS Express configuration..."

express_service_json="$(
    aws ecs describe-express-gateway-service \
        --region "${AWS_REGION}" \
        --service-arn "${ECS_SERVICE_ARN}" \
        --output json
)"

express_status="$(
    jq -r '.service.status.statusCode // empty' <<< "${express_service_json}"
)"

if [[ "${express_status}" != "ACTIVE" ]]; then
    echo "ERROR: ECS Express service is not ACTIVE."
    echo "Status: ${express_status}"
    echo "Reason:"
    jq -r '.service.status.statusReason // ""' <<< "${express_service_json}"
    exit 1
fi

#
# Get the current active configuration.
#
previous_task_definition="$(
    jq -r '
        .service.activeConfigurations
        | map(select(.taskDefinitionArn != null))
        | sort_by(.createdAt)
        | last
        | .taskDefinitionArn // empty
    ' <<< "${express_service_json}"
)"

if [[ -z "${previous_task_definition}" ]]; then
    echo "ERROR: Could not determine the current ECS Express task definition."
    echo ""
    echo "Express service response:"
    jq '.service.activeConfigurations' <<< "${express_service_json}"
    exit 1
fi

echo "Current task definition:"
echo "${previous_task_definition}"

#
# 4. Read and prepare task definition
#
echo ""
echo "4/7 Creating new immutable task-definition revision..."

aws ecs describe-task-definition \
    --region "${AWS_REGION}" \
    --task-definition "${previous_task_definition}" \
    --query taskDefinition \
    --output json > "${task_definition_file}"

echo "Checking container '${ECS_CONTAINER_NAME}'..."

container_exists="$(
    jq \
        --arg container_name "${ECS_CONTAINER_NAME}" \
        '[.containerDefinitions[] | select(.name == $container_name)] | length' \
        "${task_definition_file}"
)"

if [[ "${container_exists}" -ne 1 ]]; then
    echo "ERROR: Container '${ECS_CONTAINER_NAME}' was not found."

    echo "Available containers:"
    jq -r '.containerDefinitions[].name' "${task_definition_file}"

    exit 1
fi

echo "Container found."

jq \
    --arg container_name "${ECS_CONTAINER_NAME}" \
    --arg image "${IMAGE_URI}" '
    if any(.containerDefinitions[]; .name == $container_name) then
        .containerDefinitions |= map(
            if .name == $container_name then
                .image = $image
            else
                .
            end
        )
    else
        error("Container not found: " + $container_name)
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
    "${task_definition_file}" > "${register_file}"

new_task_definition="$(
    aws ecs register-task-definition \
        --region "${AWS_REGION}" \
        --cli-input-json "file://${register_file}" \
        --query 'taskDefinition.taskDefinitionArn' \
        --output text
)"

if [[ -z "${new_task_definition}" || "${new_task_definition}" == "None" ]]; then
    echo "ERROR: New task definition was not created."
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
    aws ecs update-express-gateway-service \
        --region "${AWS_REGION}" \
        --service-arn "${ECS_SERVICE_ARN}" \
        --task-definition-arn "${new_task_definition}" \
        --output json
)"

target_service_revision="$(
    jq -r '.service.targetConfiguration.serviceRevisionArn // empty' \
        <<< "${update_response}"
)"

if [[ -z "${target_service_revision}" ]]; then
    echo "ERROR: ECS Express did not return a target service revision."
    exit 1
fi

service_updated=true

echo "Express service update accepted."
echo "Target service revision:"
echo "${target_service_revision}"

#
# 6. Wait for EXACT deployment
#
echo ""
echo "6/7 Waiting for ECS Express deployment..."

while [[ -z "${deployment_arn}" ]]; do

    deployment_json="$(
        aws ecs list-service-deployments \
            --region "${AWS_REGION}" \
            --service "${ECS_SERVICE_ARN}" \
            --max-results 20 \
            --output json
    )"

    deployment_arn="$(
        jq -r \
            --arg revision "${target_service_revision}" '
                .serviceDeployments[]
                | select(.targetServiceRevisionArn == $revision)
                | .serviceDeploymentArn
            ' <<< "${deployment_json}" |
        head -n 1
    )"

    if [[ -z "${deployment_arn}" ]]; then
        echo "Deployment record is not available yet. Waiting..."
        sleep "${HEALTH_CHECK_INTERVAL_SECONDS}"
    fi

done

echo "Deployment record:"
echo "${deployment_arn}"

#
# Wait until AWS reports a terminal status.
#
while true; do

    deployment_details="$(
        aws ecs describe-service-deployments \
            --region "${AWS_REGION}" \
            --service-deployment-arns "${deployment_arn}" \
            --output json
    )"

    deployment_status="$(
        jq -r '.serviceDeployments[0].status // empty' \
            <<< "${deployment_details}"
    )"

    deployment_reason="$(
        jq -r '.serviceDeployments[0].statusReason // ""' \
            <<< "${deployment_details}"
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
            echo "ECS Express automatically rolled back."
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
                --show-error \
                --output /dev/null \
                --write-out '%{http_code}' \
                --max-time 10 \
                "${health_url}" || true
        )"

        echo "Health check returned HTTP ${status}"

        if [[ "${status}" == "200" ]]; then
            echo ""
            echo "Application health check passed."
            break
        fi

        sleep "${HEALTH_CHECK_INTERVAL_SECONDS}"

    done

else

    echo "HEALTH_CHECK_URL is not configured."
    echo "ECS Express deployment is the verification."

fi

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    printf 'task_definition=%s\n' \
        "${new_task_definition}" >> "${GITHUB_OUTPUT}"
fi

echo ""
echo "========================================"
echo "DEPLOYMENT SUCCESSFUL"
echo "========================================"
