#!/bin/bash
set -e

echo "=================================================="
echo "AWS AUTOMATED DEPLOYMENT SCRIPT"
echo "=================================================="

# 1. Build
echo "1. Building Docker image locally..."
docker build -t propseekr-mobile-api .

# 2. Login
echo "2. Logging into AWS ECR..."
aws ecr get-login-password --region ap-south-1 | docker login --username AWS --password-stdin 307869868474.dkr.ecr.ap-south-1.amazonaws.com

# 3. Tag
echo "3. Tagging Docker image..."
docker tag propseekr-mobile-api:latest 307869868474.dkr.ecr.ap-south-1.amazonaws.com/propseekr-mobile-api:20260722-0220

# 4. Push
echo "4. Pushing Docker image to ECR..."
docker push 307869868474.dkr.ecr.ap-south-1.amazonaws.com/propseekr-mobile-api:20260722-0220

# 5. Update ECS
echo "5. Triggering AWS ECS Service Rolling Update..."
aws ecs update-service --cluster default --service propseekr-mobile-api --force-new-deployment --region ap-south-1

echo "✓ Deployment triggered successfully!"
echo "=================================================="
