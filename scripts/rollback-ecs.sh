#!/usr/bin/env bash
# Rolls an ECS service back to an explicitly supplied, known-good task definition.
set -euo pipefail

: "${AWS_REGION:?AWS_REGION is required}"
: "${ECS_CLUSTER:?ECS_CLUSTER is required}"
: "${ECS_SERVICE:?ECS_SERVICE is required}"
: "${TASK_DEFINITION_ARN:?TASK_DEFINITION_ARN is required}"

aws ecs update-service --region "${AWS_REGION}" --cluster "${ECS_CLUSTER}" \
  --service "${ECS_SERVICE}" --task-definition "${TASK_DEFINITION_ARN}" \
  --force-new-deployment >/dev/null
aws ecs wait services-stable --region "${AWS_REGION}" --cluster "${ECS_CLUSTER}" --services "${ECS_SERVICE}"
echo "Rollback complete: ${TASK_DEFINITION_ARN}"
