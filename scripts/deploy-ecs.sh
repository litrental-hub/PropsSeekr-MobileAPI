#!/usr/bin/env bash

# Deploys one immutable ECR image to an Amazon ECS Express Mode service.
#
# Deployment flow:
#   1. Validate required deployment inputs.
#   2. Verify the immutable ECR image exists.
#   3. Discover the ECS Express service.
#   4. Read the currently active task definition.
#   5. Create a new task-definition revision with the new image.
#   6. Update the ECS Express service.
#   7. Wait for AWS to report the deployment as SUCCESSFUL or failed.
#   8. Verify the public application health endpoint.
#   9. If deployment verification fails, request rollback to the previous
#      task definition.
#
# IMPORTANT:
# - There is intentionally NO maximum deployment duration.
# - ECS Express controls the actual deployment/bake/rollback lifecycle.
# - The script waits for AWS deployment state instead of assuming a fixed
#   deployment duration.

set -euo pipefail

# ---------------------------------------------------------------------------
# Required inputs
# ---------------------------------------------------------------------------

: "${AWS_REGION:?AWS_REGION is required}"
: "${ECS_SERVICE_ARN:?ECS_SERVICE_ARN is required}"
: "${ECS_CONTAINER_NAME:?ECS_CONTAINER_NAME is required}"
: "${IMAGE_URI:?IMAGE_URI is required}"

# ---------------------------------------------------------------------------
# Optional configuration
#
# These are polling intervals only.
# They do NOT impose a maximum deployment duration.
# ---------------------------------------------------------------------------

DEPLOYMENT_POLL_INTERVAL_SECONDS="${DEPLOYMENT_POLL_INTERVAL_SECONDS:-10}"
HEALTH_CHECK_POLL_INTERVAL_SECONDS="${HEALTH_CHECK_POLL_INTERVAL_SECONDS:-10}"

readonly HEALTH_CHECK_PATH="${HEALTH_CHECK_PATH:-/hello}"

# ---------------------------------------------------------------------------
# Deployment state
# ---------------------------------------------------------------------------

service_updated=false
previous_task_definition=""
new_task_definition=""
target_service_revision=""
deployment_arn=""
deployment_status=""

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

echo "========================================"
echo "ECS Express deployment"
echo "========================================"
echo "Region:       ${AWS_REGION}"
echo "Service ARN:  ${ECS_SERVICE_ARN}"
echo "Container:    ${ECS_CONTAINER_NAME}"
echo "Image:        ${IMAGE_URI}"
echo "========================================"

# ---------------------------------------------------------------------------
# Rollback handler
# ---------------------------------------------------------------------------

rollback() {
    local status=$?

    if [[ "${service_updated}" == true && -n "${previous_task_definition}" ]]; then

        echo ""
        echo "========================================"
        echo "DEPLOYMENT FAILURE"
        echo "========================================"

        echo "Previous task definition:"
        echo "${previous_task_definition}"

        echo ""
        echo "Checking current ECS Express task definition..."

        local current_task_definition=""

        current_task_definition="$(
            aws ecs describe-express-gateway-service \
                --region "${AWS_REGION}" \
                --service-arn "${ECS_SERVICE_ARN}" \
                --query 'service.activeConfigurations[0].taskDefinitionArn' \
                --output text \
                2>/dev/null || true
        )"

        if [[ "${current_task_definition}" == "${previous_task_definition}" ]]; then

            echo "ECS Express is already using the previous task definition."
            echo "No additional rollback request is required."

        else

            echo ""
            echo "Requesting rollback to previous task definition..."

            aws ecs update-express-gateway-service \
                --region "${AWS_REGION}" \
                --service-arn "${ECS_SERVICE_ARN}" \
                --task-definition-arn "${previous_task_definition}" \
                >/dev/null || true

            echo "Rollback request submitted."

        fi
    fi

    exit "${status}"
}

trap rollback ERR

# ---------------------------------------------------------------------------
# 1. Verify ECR image
# ---------------------------------------------------------------------------

echo ""
echo "1/7 Verifying ECR image..."

image_without_registry="${IMAGE_URI#*/}"

if [[ "${image_without_registry}" == *@* ]]; then

    repository_name="${image_without_registry%%@*}"
    image_digest="${image_without_registry##*@}"

    aws ecr describe-images \
        --region "${AWS_REGION}" \
        --repository-name "${repository_name}" \
        --image-ids "imageDigest=${image_digest}" \
        >/dev/null

else

    repository_name="${image_without_registry%%:*}"
    image_tag="${image_without_registry##*:}"

    aws ecr describe-images \
        --region "${AWS_REGION}" \
        --repository-name "${repository_name}" \
        --image-ids "imageTag=${image_tag}" \
        >/dev/null

fi

echo "ECR image exists."

# ---------------------------------------------------------------------------
# 2. Discover ECS Express service
# ---------------------------------------------------------------------------

echo ""
echo "2/7 Discovering ECS Express service..."

service_json="$(
    aws ecs describe-express-gateway-service \
        --region "${AWS_REGION}" \
        --service-arn "${ECS_SERVICE_ARN}" \
        --output json
)"

service_status="$(
    jq -r '.service.status.statusCode // empty' <<< "${service_json}"
)"

if [[ "${service_status}" != "ACTIVE" ]]; then

    echo "ECS Express service is not ACTIVE." >&2
    echo "Current status: ${service_status:-unknown}" >&2

    jq '.service.status // {}' <<< "${service_json}" >&2

    exit 1
fi

actual_service="$(
    jq -r '.service.serviceName // empty' <<< "${service_json}"
)"

if [[ -z "${actual_service}" ]]; then
    echo "Unable to determine ECS Express service name." >&2
    exit 1
fi

previous_task_definition="$(
    jq -r '.service.activeConfigurations[0].taskDefinitionArn // empty' \
        <<< "${service_json}"
)"

if [[ -z "${previous_task_definition}" ]]; then
    echo "Unable to determine current ECS task definition." >&2
    exit 1
fi

echo "Express service found."
echo "Service name:"
echo "${actual_service}"

echo "Current task definition:"
echo "${previous_task_definition}"

# ---------------------------------------------------------------------------
# 3. Read current task definition
# ---------------------------------------------------------------------------

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
    ' \
    task-definition.json >/dev/null; then

    echo "Container not found:"
    echo "${ECS_CONTAINER_NAME}"

    echo ""
    echo "Available containers:"
    jq -r '.containerDefinitions[].name' task-definition.json

    exit 1
fi

echo "Container found."

# ---------------------------------------------------------------------------
# ECS Express custom task-definition compatibility validation
# ---------------------------------------------------------------------------

echo "Checking FARGATE compatibility..."

if ! jq -e '
    (.requiresCompatibilities // [])
    | index("FARGATE")
    ' \
    task-definition.json >/dev/null; then

    echo "Task definition does not contain FARGATE compatibility." >&2
    exit 1
fi

echo "Checking TCP port mapping..."

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
    ' \
    task-definition.json >/dev/null; then

    echo "Container does not have a valid named TCP port mapping." >&2
    exit 1
fi

echo "Task definition is compatible with ECS Express Mode."

# ---------------------------------------------------------------------------
# 4. Create immutable task-definition revision
# ---------------------------------------------------------------------------

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

        error(
            "Container not found in task definition: "
            + $container_name
        )

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
    task-definition.json \
    > task-definition-register.json

new_task_definition="$(
    aws ecs register-task-definition \
        --region "${AWS_REGION}" \
        --cli-input-json file://task-definition-register.json \
        --query 'taskDefinition.taskDefinitionArn' \
        --output text
)"

if [[ -z "${new_task_definition}" || "${new_task_definition}" == "None" ]]; then
    echo "AWS did not return a new task-definition ARN." >&2
    exit 1
fi

echo "New task definition:"
echo "${new_task_definition}"

# ---------------------------------------------------------------------------
# 5. Update ECS Express service
# ---------------------------------------------------------------------------

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
    jq -r '
        .service.targetConfiguration.serviceRevisionArn
        // .service.targetConfiguration.targetServiceRevisionArn
        // empty
    ' \
    <<< "${update_response}"
)"

if [[ -z "${target_service_revision}" ]]; then

    echo "ECS Express did not return the target service revision." >&2

    echo ""
    echo "AWS update response:"
    jq '.' <<< "${update_response}" >&2

    exit 1
fi

service_updated=true

echo "Express service update accepted."
echo "Target service revision:"
echo "${target_service_revision}"

# ---------------------------------------------------------------------------
# 6. Wait for the AWS deployment to finish
#
# NO MAXIMUM ATTEMPTS.
#
# ECS Express controls the actual deployment duration.
# We only poll AWS until it reaches a terminal state.
# ---------------------------------------------------------------------------

echo ""
echo "6/7 Waiting for ECS Express deployment to finish..."

while true; do

    deployment_json="$(
        aws ecs list-service-deployments \
            --region "${AWS_REGION}" \
            --service "${ECS_SERVICE_ARN}" \
            --max-results 20 \
            --output json
    )"

    deployment_arn="$(
        jq -r \
            --arg revision "${target_service_revision}" \
            '
            .serviceDeployments[]
            | select(
                .targetServiceRevisionArn == $revision
            )
            | .serviceDeploymentArn
            ' \
            <<< "${deployment_json}" \
            | head -n 1
    )"

    if [[ -z "${deployment_arn}" ]]; then

        echo "Deployment record not available yet. Waiting..."

        sleep "${DEPLOYMENT_POLL_INTERVAL_SECONDS}"

        continue
    fi

    deployment_status="$(
        jq -r \
            --arg deployment "${deployment_arn}" \
            '
            .serviceDeployments[]
            | select(
                .serviceDeploymentArn == $deployment
            )
            | .status
            ' \
            <<< "${deployment_json}" \
            | head -n 1
    )"

    deployment_reason="$(
        jq -r \
            --arg deployment "${deployment_arn}" \
            '
            .serviceDeployments[]
            | select(
                .serviceDeploymentArn == $deployment
            )
            | (.statusReason // "")
            ' \
            <<< "${deployment_json}" \
            | head -n 1
    )"

    echo "Deployment status: ${deployment_status}"

    case "${deployment_status}" in

        SUCCESSFUL)

            echo ""
            echo "ECS Express deployment completed successfully."

            break
            ;;

        PENDING|IN_PROGRESS|ROLLBACK_REQUESTED|ROLLBACK_IN_PROGRESS)

            sleep "${DEPLOYMENT_POLL_INTERVAL_SECONDS}"
            ;;

        ROLLBACK_SUCCESSFUL)

            echo ""
            echo "ECS Express automatically rolled back the deployment."
            echo "Reason: ${deployment_reason:-not provided}" >&2

            exit 1
            ;;

        ROLLBACK_FAILED)

            echo ""
            echo "ECS Express rollback FAILED."
            echo "Reason: ${deployment_reason:-not provided}" >&2

            exit 1
            ;;

        STOP_REQUESTED|STOPPED)

            echo ""
            echo "ECS Express deployment was stopped."
            echo "Reason: ${deployment_reason:-not provided}" >&2

            exit 1
            ;;

        *)

            echo ""
            echo "Unknown ECS Express deployment status:"
            echo "${deployment_status:-empty}" >&2

            echo "Reason:"
            echo "${deployment_reason:-not provided}" >&2

            exit 1
            ;;

    esac

done

# ---------------------------------------------------------------------------
# 7. Application health verification
#
# ECS Express has already reported the deployment successful.
# We then verify the public application endpoint.
#
# No fixed maximum number of health-check attempts is used.
# ---------------------------------------------------------------------------

echo ""
echo "7/7 Running application health check..."

if [[ -n "${HEALTH_CHECK_URL:-}" ]]; then

    health_url="${HEALTH_CHECK_URL%/}${HEALTH_CHECK_PATH}"

    echo "Health URL:"
    echo "${health_url}"

    while true; do

        http_status="$(
            curl \
                --silent \
                --show-error \
                --output /dev/null \
                --write-out '%{http_code}' \
                --max-time 10 \
                "${health_url}" \
                || true
        )"

        echo "Health check HTTP status: ${http_status}"

        if [[ "${http_status}" == "200" ]]; then

            echo ""
            echo "Application health check passed."

            break

        fi

        echo "Application is not returning HTTP 200 yet."
        echo "Waiting for the application..."

        sleep "${HEALTH_CHECK_POLL_INTERVAL_SECONDS}"

    done

else

    echo "HEALTH_CHECK_URL is not configured."
    echo "Skipping external application health verification."

fi

# ---------------------------------------------------------------------------
# SUCCESS
# ---------------------------------------------------------------------------

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

echo ""
echo "ECS Express deployment status:"
echo "${deployment_status}"

echo "========================================"

printf 'task_definition=%s\n' \
    "${new_task_definition}" \
    >> "${GITHUB_OUTPUT:-/dev/stdout}"
