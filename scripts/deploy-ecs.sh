#!/usr/bin/env bash
set -Eeuo pipefail

echo "========================================"
echo "ECS Production Deployment"
echo "========================================"

: "${AWS_REGION:?AWS_REGION is required}"
: "${ECS_CLUSTER:?ECS_CLUSTER is required}"
: "${ECS_SERVICE:?ECS_SERVICE is required}"
: "${ECS_CONTAINER_NAME:?ECS_CONTAINER_NAME is required}"
: "${IMAGE_URI:?IMAGE_URI is required}"

readonly HEALTH_CHECK_PATH="${HEALTH_CHECK_PATH:-/hello}"
readonly HEALTH_CHECK_INTERVAL_SECONDS="${HEALTH_CHECK_INTERVAL_SECONDS:-10}"
readonly DEPLOYMENT_TIMEOUT_SECONDS="${DEPLOYMENT_TIMEOUT_SECONDS:-600}"

service_updated=false
previous_task_definition=""
new_task_definition=""

task_definition_file="task-definition.json"
register_file="task-definition-register.json"

rollback() {
    local status=$?

    if [[ "${service_updated}" == "true" && -n "${previous_task_definition}" ]]; then
        echo ""
        echo "⚠️ Deployment failed. Rolling back to previous task definition..."
        echo "Previous task definition: ${previous_task_definition}"

        aws ecs update-service \
            --region "${AWS_REGION}" \
            --cluster "${ECS_CLUSTER}" \
            --service "${ECS_SERVICE}" \
            --task-definition "${previous_task_definition}" \
            >/dev/null 2>&1 || true
            
        echo "Rollback command sent."
    fi

    exit "${status}"
}

# Trap errors to perform rollback
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
# 3. Discover ECS service and get current task definition
#
echo ""
echo "3/7 Discovering ECS service..."
service_json="$(
    aws ecs describe-services \
        --region "${AWS_REGION}" \
        --cluster "${ECS_CLUSTER}" \
        --services "${ECS_SERVICE}" \
        --output json
)"

service_count="$(jq '.services | length' <<< "${service_json}")"
if [[ "${service_count}" -ne 1 ]]; then
    echo "ERROR: ECS service was not found."
    echo "Cluster: ${ECS_CLUSTER}"
    echo "Service: ${ECS_SERVICE}"
    exit 1
fi

service_status="$(jq -r '.services[0].status' <<< "${service_json}")"
if [[ "${service_status}" != "ACTIVE" ]]; then
    echo "ERROR: ECS service is not ACTIVE (status: ${service_status})."
    exit 1
fi

previous_task_definition="$(jq -r '.services[0].taskDefinition // empty' <<< "${service_json}")"
if [[ -z "${previous_task_definition}" ]]; then
    echo "ERROR: Could not determine the current active task definition."
    exit 1
fi

echo "Current task definition: ${previous_task_definition}"

#
# 4. Read and prepare task definition
#
echo ""
echo "4/7 Creating new task-definition revision..."
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
    echo "ERROR: Container '${ECS_CONTAINER_NAME}' was not found in task definition."
    echo "Available containers:"
    jq -r '.containerDefinitions[].name' "${task_definition_file}"
    exit 1
fi

jq \
    --arg container_name "${ECS_CONTAINER_NAME}" \
    --arg image "${IMAGE_URI}" '
    .containerDefinitions |= map(
        if .name == $container_name then
            .image = $image
        else
            .
        end
    )
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
    echo "ERROR: New task definition registration failed."
    exit 1
fi

echo "New task definition registered: ${new_task_definition}"

#
# 5. Update ECS service to deploy the new task definition
#
echo ""
echo "5/7 Updating ECS service..."
update_response="$(
    aws ecs update-service \
        --region "${AWS_REGION}" \
        --cluster "${ECS_CLUSTER}" \
        --service "${ECS_SERVICE}" \
        --task-definition "${new_task_definition}" \
        --output json
)"

service_updated=true
echo "ECS service update command sent successfully."

#
# 6. Wait for service deployment to stabilize with live event feedback
#
echo ""
echo "6/7 Monitoring ECS deployment progress..."

start_time=$(date +%s)
last_event_timestamp=""

while true; do
    current_time=$(date +%s)
    elapsed=$((current_time - start_time))
    
    if [[ $elapsed -ge $DEPLOYMENT_TIMEOUT_SECONDS ]]; then
        echo "ERROR: Deployment timed out after $((DEPLOYMENT_TIMEOUT_SECONDS / 60)) minutes."
        exit 1
    fi

    # Describe the service status
    current_status_json="$(
        aws ecs describe-services \
            --region "${AWS_REGION}" \
            --cluster "${ECS_CLUSTER}" \
            --services "${ECS_SERVICE}" \
            --output json
    )"

    # Get primary (newest) deployment details
    primary_deployment="$(jq -r '.services[0].deployments[] | select(.status == "PRIMARY")' <<< "${current_status_json}")"
    running_count=$(jq -r '.runningCount // 0' <<< "${primary_deployment}")
    desired_count=$(jq -r '.desiredCount // 0' <<< "${primary_deployment}")
    
    echo "Progress: ${running_count}/${desired_count} tasks running (Elapsed: ${elapsed}s)..."

    # Print any new ECS events
    events_json="$(jq '.services[0].events' <<< "${current_status_json}")"
    if [[ -n "${events_json}" && "${events_json}" != "null" ]]; then
        new_events="$(jq -c --arg last_ts "${last_event_timestamp}" '
            .[] | select($last_ts == "" or .createdAt > $last_ts)
        ' <<< "${events_json}" 2>/dev/null || true)"
        
        if [[ -n "${new_events}" ]]; then
            # Convert to array of events and reverse to show oldest first
            reversed_events="$(jq -s 'reverse | .[]' <<< "${new_events}" 2>/dev/null || true)"
            if [[ -n "${reversed_events}" ]]; then
                while read -r event; do
                    if [[ -n "${event}" ]]; then
                        msg="$(jq -r '.message' <<< "${event}")"
                        ts="$(jq -r '.createdAt' <<< "${event}")"
                        echo "  → [ECS Event $ts] $msg"
                        last_event_timestamp="${ts}"
                    fi
                done <<< "${reversed_events}"
            fi
        fi
    fi

    # Check for success
    if [[ "${running_count}" -eq "${desired_count}" ]]; then
        # Check if old deployments are completely gone (fully drained)
        active_deployments_count=$(jq '.services[0].deployments | map(select(.status != "PRIMARY")) | length' <<< "${current_status_json}")
        if [[ "${active_deployments_count}" -eq 0 ]]; then
            echo "✅ Deployment completed successfully! Service is stable."
            break
        fi
        echo "Draining old container tasks (${active_deployments_count} old deployments still active)..."
    fi

    sleep "${HEALTH_CHECK_INTERVAL_SECONDS}"
done

#
# 7. Application health check
#
echo ""
echo "7/7 Verifying application health..."

if [[ -n "${HEALTH_CHECK_URL:-}" ]]; then
    health_url="${HEALTH_CHECK_URL%/}${HEALTH_CHECK_PATH}"
    echo "Health check URL: ${health_url}"

    # Wait for the HTTP endpoint to return 200 OK
    max_health_checks=30
    health_check_count=0
    
    while true; do
        health_check_count=$((health_check_count + 1))
        
        status="$(
            curl \
                --silent \
                --show-error \
                --output /dev/null \
                --write-out '%{http_code}' \
                --max-time 10 \
                "${health_url}" || true
        )"

        echo "Health check attempt ${health_check_count}/${max_health_checks} returned HTTP ${status}"

        if [[ "${status}" == "200" ]]; then
            echo "✅ Application health check passed!"
            break
        fi

        if [[ ${health_check_count} -ge ${max_health_checks} ]]; then
            echo "ERROR: Health check failed to return HTTP 200 after ${max_health_checks} attempts."
            exit 1
        fi

        sleep "${HEALTH_CHECK_INTERVAL_SECONDS}"
    done
else
    echo "HEALTH_CHECK_URL is not configured. ECS stability is the deployment verification."
fi

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    printf 'task_definition=%s\n' "${new_task_definition}" >> "${GITHUB_OUTPUT}"
fi

echo ""
echo "========================================"
echo "DEPLOYMENT SUCCESSFUL"
echo "========================================"
