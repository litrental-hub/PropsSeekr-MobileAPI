#!/usr/bin/env bash
# Deploys one immutable ECR image by registering a new ECS task-definition revision.
# On failure after service update, it restores the previous task definition.
set -euo pipefail

: "${AWS_REGION:?AWS_REGION is required}"
: "${ECS_CLUSTER:?ECS_CLUSTER is required}"
: "${ECS_SERVICE:?ECS_SERVICE is required}"
: "${ECS_CONTAINER_NAME:?ECS_CONTAINER_NAME is required}"
: "${IMAGE_URI:?IMAGE_URI is required}"

readonly HEALTH_CHECK_PATH="${HEALTH_CHECK_PATH:-/hello}"
readonly HEALTH_CHECK_ATTEMPTS="${HEALTH_CHECK_ATTEMPTS:-30}"
readonly HEALTH_CHECK_INTERVAL_SECONDS="${HEALTH_CHECK_INTERVAL_SECONDS:-10}"

previous_task_definition=""
service_updated=false

rollback() {
  local status=$?
  if [[ "${service_updated}" == true && -n "${previous_task_definition}" ]]; then
    echo "Deployment failed; rolling back to ${previous_task_definition}." >&2
    aws ecs update-service \
      --region "${AWS_REGION}" \
      --cluster "${ECS_CLUSTER}" \
      --service "${ECS_SERVICE}" \
      --task-definition "${previous_task_definition}" >/dev/null || true
  fi
  exit "${status}"
}
trap rollback ERR

image_tag="${IMAGE_URI##*:}"
repository_name="${IMAGE_URI#*/}"
repository_name="${repository_name%%:*}"
aws ecr describe-images --region "${AWS_REGION}" --repository-name "${repository_name}" --image-ids "imageTag=${image_tag}" >/dev/null

previous_task_definition="$(aws ecs describe-services \
  --region "${AWS_REGION}" --cluster "${ECS_CLUSTER}" --services "${ECS_SERVICE}" \
  --query 'services[0].taskDefinition' --output text)"
[[ "${previous_task_definition}" != "None" ]] || { echo 'ECS service was not found.' >&2; exit 1; }

aws ecs describe-task-definition --region "${AWS_REGION}" \
  --task-definition "${previous_task_definition}" --query taskDefinition > task-definition.json

jq --arg container_name "${ECS_CONTAINER_NAME}" --arg image "${IMAGE_URI}" '
  if any(.containerDefinitions[]; .name == $container_name) then
    .containerDefinitions |= map(if .name == $container_name then .image = $image else . end)
  else error("Container not found in task definition: " + $container_name) end
  | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities,
        .registeredAt, .registeredBy, .deregisteredAt)
' task-definition.json > task-definition-register.json

new_task_definition="$(aws ecs register-task-definition --region "${AWS_REGION}" \
  --cli-input-json file://task-definition-register.json \
  --query 'taskDefinition.taskDefinitionArn' --output text)"

aws ecs update-service --region "${AWS_REGION}" --cluster "${ECS_CLUSTER}" \
  --service "${ECS_SERVICE}" --task-definition "${new_task_definition}" \
  --force-new-deployment >/dev/null
service_updated=true

aws ecs wait services-stable --region "${AWS_REGION}" --cluster "${ECS_CLUSTER}" --services "${ECS_SERVICE}"

if [[ -n "${HEALTH_CHECK_URL:-}" ]]; then
  for ((attempt = 1; attempt <= HEALTH_CHECK_ATTEMPTS; attempt++)); do
    status="$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 10 "${HEALTH_CHECK_URL%/}${HEALTH_CHECK_PATH}" || true)"
    if [[ "${status}" == '200' ]]; then
      echo 'Application health check passed.'
      printf 'task_definition=%s\n' "${new_task_definition}" >> "${GITHUB_OUTPUT:-/dev/stdout}"
      exit 0
    fi
    echo "Health check ${attempt}/${HEALTH_CHECK_ATTEMPTS} returned ${status:-000}."
    sleep "${HEALTH_CHECK_INTERVAL_SECONDS}"
  done
  echo 'Application health check did not pass.' >&2
  exit 1
fi

echo 'HEALTH_CHECK_URL is not configured; ECS health is the only verification.' >&2
printf 'task_definition=%s\n' "${new_task_definition}" >> "${GITHUB_OUTPUT:-/dev/stdout}"
