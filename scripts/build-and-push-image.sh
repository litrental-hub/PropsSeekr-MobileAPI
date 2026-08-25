#!/usr/bin/env bash
# Builds and pushes an immutable image. AWS credentials must already be supplied
# by the caller (GitHub Actions uses OIDC).
set -euo pipefail

: "${AWS_REGION:?AWS_REGION is required}"
: "${AWS_ACCOUNT_ID:?AWS_ACCOUNT_ID is required}"
: "${ECR_REPOSITORY:?ECR_REPOSITORY is required}"
: "${GITHUB_SHA:?GITHUB_SHA is required}"

readonly ECR_REGISTRY="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"
readonly IMAGE_TAG="sha-${GITHUB_SHA}"
readonly IMAGE_URI="${ECR_REGISTRY}/${ECR_REPOSITORY}:${IMAGE_TAG}"

aws ecr describe-repositories --repository-names "${ECR_REPOSITORY}" >/dev/null
aws ecr get-login-password --region "${AWS_REGION}" | docker login --username AWS --password-stdin "${ECR_REGISTRY}"

docker build --pull --tag "${IMAGE_URI}" .
docker push "${IMAGE_URI}"

printf 'image_uri=%s\n' "${IMAGE_URI}" >> "${GITHUB_OUTPUT:-/dev/stdout}"
printf 'image_tag=%s\n' "${IMAGE_TAG}" >> "${GITHUB_OUTPUT:-/dev/stdout}"
