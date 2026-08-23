#!/usr/bin/env bash
#
# Rollback PropSeekr-MobileAPI to a previous ECR image.
#
# Usage:
#   ./scripts/rollback.sh                        # Interactive — lists tags
#   ./scripts/rollback.sh --tag 20260822-1530-abc1234  # Specific tag
#   ./scripts/rollback.sh --tag 20260822-1530-abc1234 --force
#
set -euo pipefail

# ─── Configuration ───────────────────────────────────────────
AWS_REGION="ap-south-1"
AWS_ACCOUNT_ID="307869868474"
ECR_REPO="propseekr-mobile-api"
ECS_CLUSTER="default"
ECS_SERVICE="propseekr-mobile-api"
ECR_REGISTRY="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"

# ─── Parse Args ──────────────────────────────────────────────
TAG=""
FORCE=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag)   TAG="$2"; shift 2 ;;
    --force) FORCE=true; shift ;;
    *)       shift ;;
  esac
done

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
GRAY='\033[0;90m'
NC='\033[0m'

echo ""
echo -e "${YELLOW}══════════════════════════════════════════════════════════${NC}"
echo -e "${YELLOW}  PROPSEEKR MOBILE API — ROLLBACK${NC}"
echo -e "${YELLOW}══════════════════════════════════════════════════════════${NC}"

# ─── Current State ───────────────────────────────────────────
echo ""
echo -e "${CYAN}[1] Current deployment state${NC}"
echo -e "${GRAY}$(printf '─%.0s' {1..60})${NC}"

CURRENT_TASK_DEF=$(aws ecs describe-services --cluster "${ECS_CLUSTER}" --services "${ECS_SERVICE}" \
  --query 'services[0].taskDefinition' --output text --region "${AWS_REGION}")
CURRENT_IMAGE=$(aws ecs describe-task-definition --task-definition "${CURRENT_TASK_DEF}" \
  --query 'taskDefinition.containerDefinitions[0].image' --output text --region "${AWS_REGION}")

echo "  Current task def : ${CURRENT_TASK_DEF}"
echo "  Current image    : ${CURRENT_IMAGE}"

# ─── List Tags ───────────────────────────────────────────────
echo ""
echo -e "${CYAN}[2] Available image tags (most recent first)${NC}"
echo -e "${GRAY}$(printf '─%.0s' {1..60})${NC}"

IMAGES_JSON=$(aws ecr describe-images --repository-name "${ECR_REPO}" --region "${AWS_REGION}" \
  --query 'sort_by(imageDetails,& imagePushedAt)[-15:]' --output json)

TAGS=()
INDEX=0
while IFS= read -r line; do
  PUSH_TIME=$(echo "$line" | jq -r '.imagePushedAt')
  SIZE=$(echo "$line" | jq -r '.imageSizeInBytes')
  SIZE_MB=$(echo "scale=1; ${SIZE} / 1048576" | bc 2>/dev/null || echo "?")
  IMG_TAGS=$(echo "$line" | jq -r '.imageTags // [] | map(select(. != "latest")) | .[]' 2>/dev/null)

  if [ -z "${IMG_TAGS}" ]; then continue; fi

  PRIMARY_TAG=$(echo "${IMG_TAGS}" | head -1)
  TAGS+=("${PRIMARY_TAG}")
  INDEX=$((INDEX + 1))

  MARKER=""
  if echo "${CURRENT_IMAGE}" | grep -q ":${PRIMARY_TAG}$"; then
    MARKER=" ← CURRENT"
    echo -e "  ${GREEN}[${INDEX}] ${PRIMARY_TAG}  (${SIZE_MB} MB, pushed ${PUSH_TIME})${MARKER}${NC}"
  else
    echo "  [${INDEX}] ${PRIMARY_TAG}  (${SIZE_MB} MB, pushed ${PUSH_TIME})"
  fi
done < <(echo "${IMAGES_JSON}" | jq -c '.[] | select(.imageTags != null)' | tac)

if [ -z "${TAG}" ]; then
  echo ""
  read -rp "  Enter number to rollback to (or 'q' to quit): " SELECTION
  if [ "${SELECTION}" = "q" ] || [ -z "${SELECTION}" ]; then
    echo "  Cancelled."
    exit 0
  fi

  SEL_INDEX=$((SELECTION - 1))
  if [ $SEL_INDEX -lt 0 ] || [ $SEL_INDEX -ge ${#TAGS[@]} ]; then
    echo -e "  ${RED}❌ Invalid selection.${NC}"
    exit 1
  fi
  TAG="${TAGS[$SEL_INDEX]}"
fi

ROLLBACK_IMAGE="${ECR_REGISTRY}/${ECR_REPO}:${TAG}"

# ─── Confirm ────────────────────────────────────────────────
echo ""
echo -e "${CYAN}[3] Rollback confirmation${NC}"
echo -e "${GRAY}$(printf '─%.0s' {1..60})${NC}"
echo -e "  ${RED}FROM : ${CURRENT_IMAGE}${NC}"
echo -e "  ${GREEN}TO   : ${ROLLBACK_IMAGE}${NC}"

if ! $FORCE; then
  read -rp "  Proceed with rollback? (yes/no): " CONFIRM
  if [ "${CONFIRM}" != "yes" ]; then
    echo -e "  ${YELLOW}Cancelled.${NC}"
    exit 0
  fi
fi

# ─── Execute Rollback ───────────────────────────────────────
echo ""
echo -e "${CYAN}[4] Executing rollback${NC}"
echo -e "${GRAY}$(printf '─%.0s' {1..60})${NC}"

aws ecs describe-task-definition --task-definition "${CURRENT_TASK_DEF}" \
  --query 'taskDefinition' --region "${AWS_REGION}" > /tmp/rollback-task-def.json

jq --arg IMAGE "${ROLLBACK_IMAGE}" \
  '.containerDefinitions[0].image = $IMAGE | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)' \
  /tmp/rollback-task-def.json > /tmp/register-rollback.json

NEW_ARN=$(aws ecs register-task-definition \
  --cli-input-json file:///tmp/register-rollback.json \
  --query 'taskDefinition.taskDefinitionArn' --output text --region "${AWS_REGION}")

echo -e "  ${GREEN}✅ Registered task definition: ${NEW_ARN}${NC}"

aws ecs update-service --cluster "${ECS_CLUSTER}" --service "${ECS_SERVICE}" \
  --task-definition "${NEW_ARN}" --force-new-deployment --region "${AWS_REGION}" \
  --query 'service.serviceName' --output text >/dev/null

echo -e "  ${GREEN}✅ Rollback triggered.${NC}"

echo -e "  ${GRAY}⏳ Waiting for service to stabilize...${NC}"
aws ecs wait services-stable --cluster "${ECS_CLUSTER}" --services "${ECS_SERVICE}" --region "${AWS_REGION}" 2>/dev/null || true
echo -e "  ${GREEN}✅ Service stabilized.${NC}"

echo ""
echo -e "${GREEN}══════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}  ✅ ROLLBACK COMPLETE${NC}"
echo -e "${GREEN}  Image: ${ROLLBACK_IMAGE}${NC}"
echo -e "${GREEN}══════════════════════════════════════════════════════════${NC}"
