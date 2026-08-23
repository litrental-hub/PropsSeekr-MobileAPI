#!/usr/bin/env bash
#
# Production-grade deployment script for PropSeekr-MobileAPI.
# Builds Docker image, pushes to AWS ECR, and triggers ECS rolling update.
#
# Usage:
#   ./scripts/deploy.sh              # Full deploy
#   ./scripts/deploy.sh --dry-run    # Preview only
#   ./scripts/deploy.sh --skip-health-check
#
set -euo pipefail

# ─── Configuration ───────────────────────────────────────────
AWS_REGION="ap-south-1"
AWS_ACCOUNT_ID="307869868474"
ECR_REPO="propseekr-mobile-api"
ECS_CLUSTER="default"
ECS_SERVICE="propseekr-mobile-api"
LOCAL_IMAGE="propseekr-mobile-api"
ECR_REGISTRY="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"
HEALTH_CHECK_URL="${HEALTH_CHECK_URL:-}"

# ─── Dynamic Tagging ────────────────────────────────────────
TIMESTAMP=$(date -u +"%Y%m%d-%H%M")
GIT_SHA=$(git rev-parse --short HEAD 2>/dev/null || echo "unknown")
IMAGE_TAG="${TIMESTAMP}-${GIT_SHA}"

# ─── Flags ───────────────────────────────────────────────────
DRY_RUN=false
SKIP_HEALTH=false
for arg in "$@"; do
  case "$arg" in
    --dry-run)          DRY_RUN=true ;;
    --skip-health-check) SKIP_HEALTH=true ;;
  esac
done

# ─── Helpers ─────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
MAGENTA='\033[0;35m'
GRAY='\033[0;90m'
NC='\033[0m'

step()  { echo -e "\n${CYAN}[$1] $2${NC}"; echo -e "${GRAY}$(printf '─%.0s' {1..60})${NC}"; }
ok()    { echo -e "  ${GREEN}✅ $1${NC}"; }
warn()  { echo -e "  ${YELLOW}⚠️  $1${NC}"; }
fail()  { echo -e "  ${RED}❌ $1${NC}"; }

run_step() {
  if $DRY_RUN; then
    echo -e "  ${YELLOW}[DRY RUN] Would execute: $1${NC}"
  else
    eval "$2"
  fi
}

DEPLOY_START=$(date +%s)

echo ""
echo -e "${MAGENTA}══════════════════════════════════════════════════════════${NC}"
echo -e "${MAGENTA}  PROPSEEKR MOBILE API — DEPLOYMENT${NC}"
echo -e "${MAGENTA}══════════════════════════════════════════════════════════${NC}"
echo "  Image Tag : ${IMAGE_TAG}"
echo "  Git SHA   : ${GIT_SHA}"
echo "  Registry  : ${ECR_REGISTRY}/${ECR_REPO}"
echo "  Cluster   : ${ECS_CLUSTER} / ${ECS_SERVICE}"
$DRY_RUN && echo -e "  Mode      : ${YELLOW}DRY RUN${NC}"
echo -e "${MAGENTA}══════════════════════════════════════════════════════════${NC}"

# ─── Step 1: Pre-flight Checks ──────────────────────────────
step "1/7" "Pre-flight checks"

command -v docker >/dev/null 2>&1 || { fail "Docker not installed."; exit 1; }
ok "Docker: $(docker --version)"

docker info >/dev/null 2>&1 || { fail "Docker daemon not running."; exit 1; }
ok "Docker daemon running."

command -v aws >/dev/null 2>&1 || { fail "AWS CLI not installed."; exit 1; }
ok "AWS CLI: $(aws --version 2>&1 | head -1)"

AWS_IDENTITY=$(aws sts get-caller-identity --output json 2>/dev/null) || { fail "AWS credentials not configured."; exit 1; }
AWS_ACCT=$(echo "$AWS_IDENTITY" | jq -r '.Account')
ok "AWS Account: ${AWS_ACCT}"

if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
  warn "Working directory has uncommitted changes!"
fi

# ─── Step 2: Docker Build ───────────────────────────────────
step "2/7" "Building Docker image"
run_step "Docker build" "docker build -t ${LOCAL_IMAGE}:${IMAGE_TAG} -t ${LOCAL_IMAGE}:latest ."
ok "Built ${LOCAL_IMAGE}:${IMAGE_TAG}"

# ─── Step 3: ECR Login ──────────────────────────────────────
step "3/7" "Logging into AWS ECR"
run_step "ECR login" "aws ecr get-login-password --region ${AWS_REGION} | docker login --username AWS --password-stdin ${ECR_REGISTRY}"
ok "Authenticated with ECR."

# ─── Step 4: Tag Images ─────────────────────────────────────
step "4/7" "Tagging images for ECR"
for tag in "${IMAGE_TAG}" "${GIT_SHA}" "latest"; do
  run_step "Tag :${tag}" "docker tag ${LOCAL_IMAGE}:${IMAGE_TAG} ${ECR_REGISTRY}/${ECR_REPO}:${tag}"
  ok "Tagged → ${ECR_REGISTRY}/${ECR_REPO}:${tag}"
done

# ─── Step 5: Push to ECR ────────────────────────────────────
step "5/7" "Pushing images to ECR"
for tag in "${IMAGE_TAG}" "${GIT_SHA}" "latest"; do
  run_step "Push :${tag}" "docker push ${ECR_REGISTRY}/${ECR_REPO}:${tag}"
  ok "Pushed :${tag}"
done

# ─── Step 6: Deploy to ECS ──────────────────────────────────
step "6/7" "Deploying to ECS (rolling update)"

# Snapshot current state
echo -e "  ${GRAY}📸 Snapshotting current deployment...${NC}"
CURRENT_TASK_DEF=$(aws ecs describe-services --cluster "${ECS_CLUSTER}" --services "${ECS_SERVICE}" \
  --query 'services[0].taskDefinition' --output text --region "${AWS_REGION}" 2>/dev/null || echo "")

if [ -n "${CURRENT_TASK_DEF}" ]; then
  CURRENT_IMAGE=$(aws ecs describe-task-definition --task-definition "${CURRENT_TASK_DEF}" \
    --query 'taskDefinition.containerDefinitions[0].image' --output text --region "${AWS_REGION}" 2>/dev/null || echo "unknown")
  echo -e "  ${GRAY}Previous image: ${CURRENT_IMAGE}${NC}"
fi

NEW_IMAGE="${ECR_REGISTRY}/${ECR_REPO}:${IMAGE_TAG}"

if ! $DRY_RUN && [ -n "${CURRENT_TASK_DEF}" ] && command -v jq >/dev/null 2>&1; then
  # Get, update, and register new task definition
  aws ecs describe-task-definition --task-definition "${CURRENT_TASK_DEF}" \
    --query 'taskDefinition' --region "${AWS_REGION}" > /tmp/task-def.json

  jq --arg IMAGE "${NEW_IMAGE}" \
    '.containerDefinitions[0].image = $IMAGE | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)' \
    /tmp/task-def.json > /tmp/register-task-def.json

  NEW_TASK_DEF_ARN=$(aws ecs register-task-definition \
    --cli-input-json file:///tmp/register-task-def.json \
    --query 'taskDefinition.taskDefinitionArn' --output text --region "${AWS_REGION}")

  aws ecs update-service \
    --cluster "${ECS_CLUSTER}" --service "${ECS_SERVICE}" \
    --task-definition "${NEW_TASK_DEF_ARN}" \
    --force-new-deployment --region "${AWS_REGION}" \
    --query 'service.serviceName' --output text
else
  run_step "ECS update" \
    "aws ecs update-service --cluster ${ECS_CLUSTER} --service ${ECS_SERVICE} --force-new-deployment --region ${AWS_REGION} --query 'service.serviceName' --output text"
fi

ok "ECS rolling update triggered."

# Wait for stability
echo -e "  ${GRAY}⏳ Waiting for service to stabilize...${NC}"
if ! $DRY_RUN; then
  aws ecs wait services-stable --cluster "${ECS_CLUSTER}" --services "${ECS_SERVICE}" --region "${AWS_REGION}" 2>/dev/null || true
  ok "Service stabilized."
fi

# ─── Step 7: Health Check ────────────────────────────────────
step "7/7" "Post-deployment health check"

if $SKIP_HEALTH; then
  warn "Health check skipped (--skip-health-check flag)."
elif [ -z "${HEALTH_CHECK_URL}" ]; then
  warn "HEALTH_CHECK_URL not set. Export it to enable health verification."
  echo -e "    ${GRAY}export HEALTH_CHECK_URL='https://your-alb-url.com'${NC}"
elif ! $DRY_RUN; then
  MAX_RETRIES=20
  RETRY_INTERVAL=15
  HEALTHY=false

  for i in $(seq 1 $MAX_RETRIES); do
    HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 "${HEALTH_CHECK_URL}/hello" 2>/dev/null || echo "000")
    if [ "${HTTP_STATUS}" = "200" ]; then
      RESPONSE=$(curl -s --max-time 10 "${HEALTH_CHECK_URL}/hello" 2>/dev/null)
      ok "Health check PASSED! (Status: ${HTTP_STATUS}, Body: ${RESPONSE})"
      HEALTHY=true
      break
    fi
    echo -e "    ${GRAY}Attempt ${i}/${MAX_RETRIES} — Status: ${HTTP_STATUS}. Retrying in ${RETRY_INTERVAL}s...${NC}"
    sleep $RETRY_INTERVAL
  done

  if ! $HEALTHY; then
    fail "Health check FAILED after ${MAX_RETRIES} attempts!"
    echo -e "    ${YELLOW}Consider rolling back: ./scripts/rollback.sh${NC}"
  fi
fi

# ─── Summary ─────────────────────────────────────────────────
DEPLOY_END=$(date +%s)
DURATION=$((DEPLOY_END - DEPLOY_START))

echo ""
echo -e "${GREEN}══════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}  ✅ DEPLOYMENT COMPLETE${NC}"
echo -e "${GREEN}══════════════════════════════════════════════════════════${NC}"
echo "  Image     : ${ECR_REGISTRY}/${ECR_REPO}:${IMAGE_TAG}"
echo "  Git SHA   : ${GIT_SHA}"
echo "  Cluster   : ${ECS_CLUSTER} / ${ECS_SERVICE}"
echo "  Duration  : ${DURATION}s"
echo "  Rollback  : ./scripts/rollback.sh"
echo -e "${GREEN}══════════════════════════════════════════════════════════${NC}"
