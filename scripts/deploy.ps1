Write-Host "=================================================="
Write-Host "AWS AUTOMATED DEPLOYMENT SCRIPT"
Write-Host "=================================================="

# 1. Build
Write-Host "1. Building Docker image locally..."
docker build -t propseekr-mobile-api .
if ($LASTEXITCODE -ne 0) { 
    Write-Error "Docker build failed."
    exit $LASTEXITCODE 
}

# 2. Login
Write-Host "2. Logging into AWS ECR..."
aws ecr get-login-password --region ap-south-1 | docker login --username AWS --password-stdin 307869868474.dkr.ecr.ap-south-1.amazonaws.com
if ($LASTEXITCODE -ne 0) { 
    Write-Error "ECR login failed."
    exit $LASTEXITCODE 
}

# 3. Tag
Write-Host "3. Tagging Docker image..."
docker tag propseekr-mobile-api:latest 307869868474.dkr.ecr.ap-south-1.amazonaws.com/propseekr-mobile-api:20260722-0220
if ($LASTEXITCODE -ne 0) { 
    Write-Error "Docker tag failed."
    exit $LASTEXITCODE 
}

# 4. Push
Write-Host "4. Pushing Docker image to ECR..."
docker push 307869868474.dkr.ecr.ap-south-1.amazonaws.com/propseekr-mobile-api:20260722-0220
if ($LASTEXITCODE -ne 0) { 
    Write-Error "Docker push failed."
    exit $LASTEXITCODE 
}

# 5. Update ECS
Write-Host "5. Triggering AWS ECS Service Rolling Update..."
aws ecs update-service --cluster default --service propseekr-mobile-api --force-new-deployment --region ap-south-1
if ($LASTEXITCODE -ne 0) { 
    Write-Error "ECS service update failed."
    exit $LASTEXITCODE 
}

Write-Host "Deployment triggered successfully!"
Write-Host "=================================================="
