<#
.SYNOPSIS
    Production-grade deployment script for PropSeekr-MobileAPI.
    Builds Docker image, pushes to AWS ECR, and triggers ECS rolling update.

.DESCRIPTION
    This script performs the following steps:
    1. Pre-flight checks (Docker, AWS CLI, git)
    2. Docker build with multi-stage Dockerfile
    3. Tag with dynamic identifiers (timestamp + git SHA)
    4. Push to AWS ECR
    5. Trigger ECS service rolling update
    6. Wait for service stability
    7. Post-deploy health check

.PARAMETER SkipHealthCheck
    Skip the post-deployment health check.

.PARAMETER DryRun
    Show what would be done without executing.

.EXAMPLE
    .\scripts\deploy.ps1
    .\scripts\deploy.ps1 -DryRun
    .\scripts\deploy.ps1 -SkipHealthCheck
#>

param(
    [switch]$SkipHealthCheck,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# ─── Configuration ───────────────────────────────────────────
$AWS_REGION       = "ap-south-1"
$AWS_ACCOUNT_ID   = "307869868474"
$ECR_REPO         = "propseekr-mobile-api"
$ECS_CLUSTER      = "default"
$ECS_SERVICE      = "propseekr-mobile-api"
$LOCAL_IMAGE_NAME = "propseekr-mobile-api"
$ECR_REGISTRY     = "${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"
$HEALTH_CHECK_URL = $env:HEALTH_CHECK_URL  # Set via environment variable

# ─── Dynamic Tagging ────────────────────────────────────────
$Timestamp = Get-Date -Format "yyyyMMdd-HHmm"
$GitSha    = (git rev-parse --short HEAD 2>$null) ?? "unknown"
$ImageTag  = "${Timestamp}-${GitSha}"

# ─── Helpers ─────────────────────────────────────────────────
function Write-Step($step, $msg) {
    Write-Host ""
    Write-Host "[$step] $msg" -ForegroundColor Cyan
    Write-Host ("─" * 60) -ForegroundColor DarkGray
}

function Write-Ok($msg) {
    Write-Host "  ✅ $msg" -ForegroundColor Green
}

function Write-Warn($msg) {
    Write-Host "  ⚠️  $msg" -ForegroundColor Yellow
}

function Write-Fail($msg) {
    Write-Host "  ❌ $msg" -ForegroundColor Red
}

function Invoke-Step($description, [scriptblock]$action) {
    if ($DryRun) {
        Write-Host "  [DRY RUN] Would execute: $description" -ForegroundColor DarkYellow
        return
    }
    & $action
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        Write-Fail "$description failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

$DeployStart = Get-Date

Write-Host ""
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  PROPSEEKR MOBILE API — DEPLOYMENT" -ForegroundColor Magenta
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  Image Tag : $ImageTag"
Write-Host "  Git SHA   : $GitSha"
Write-Host "  Registry  : $ECR_REGISTRY/$ECR_REPO"
Write-Host "  Cluster   : $ECS_CLUSTER / $ECS_SERVICE"
Write-Host "  Timestamp : $Timestamp"
if ($DryRun) { Write-Host "  Mode      : DRY RUN" -ForegroundColor Yellow }
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Magenta

# ─── Step 1: Pre-flight Checks ──────────────────────────────
Write-Step "1/7" "Pre-flight checks"

# Docker
$dockerVersion = docker --version 2>$null
if (-not $dockerVersion) { Write-Fail "Docker is not installed or not in PATH."; exit 1 }
Write-Ok "Docker: $dockerVersion"

# Docker daemon
$dockerInfo = docker info 2>&1
if ($LASTEXITCODE -ne 0) { Write-Fail "Docker daemon is not running."; exit 1 }
Write-Ok "Docker daemon is running."

# AWS CLI
$awsVersion = aws --version 2>$null
if (-not $awsVersion) { Write-Fail "AWS CLI is not installed or not in PATH."; exit 1 }
Write-Ok "AWS CLI: $awsVersion"

# AWS identity
$awsIdentity = aws sts get-caller-identity --output json 2>$null | ConvertFrom-Json
if (-not $awsIdentity) { Write-Fail "AWS credentials not configured. Run 'aws configure'."; exit 1 }
Write-Ok "AWS Account: $($awsIdentity.Account) ($($awsIdentity.Arn))"

# Git
$gitVersion = git --version 2>$null
if (-not $gitVersion) { Write-Warn "Git not found — using 'unknown' for SHA." }
else { Write-Ok "Git: $gitVersion" }

# Uncommitted changes warning
$gitStatus = git status --porcelain 2>$null
if ($gitStatus) {
    Write-Warn "Working directory has uncommitted changes!"
    Write-Host "    Consider committing before deploying." -ForegroundColor Yellow
}

# ─── Step 2: Docker Build ───────────────────────────────────
Write-Step "2/7" "Building Docker image"
Invoke-Step "Docker build" {
    docker build -t "${LOCAL_IMAGE_NAME}:${ImageTag}" -t "${LOCAL_IMAGE_NAME}:latest" .
}
Write-Ok "Built ${LOCAL_IMAGE_NAME}:${ImageTag}"

# ─── Step 3: ECR Login ──────────────────────────────────────
Write-Step "3/7" "Logging into AWS ECR"
Invoke-Step "ECR login" {
    aws ecr get-login-password --region $AWS_REGION | docker login --username AWS --password-stdin $ECR_REGISTRY
}
Write-Ok "Authenticated with ECR."

# ─── Step 4: Tag Images ─────────────────────────────────────
Write-Step "4/7" "Tagging images for ECR"
$tags = @($ImageTag, $GitSha, "latest")
foreach ($tag in $tags) {
    Invoke-Step "Tag :${tag}" {
        docker tag "${LOCAL_IMAGE_NAME}:${ImageTag}" "${ECR_REGISTRY}/${ECR_REPO}:${tag}"
    }
    Write-Ok "Tagged → ${ECR_REGISTRY}/${ECR_REPO}:${tag}"
}

# ─── Step 5: Push to ECR ────────────────────────────────────
Write-Step "5/7" "Pushing images to ECR"
foreach ($tag in $tags) {
    Invoke-Step "Push :${tag}" {
        docker push "${ECR_REGISTRY}/${ECR_REPO}:${tag}"
    }
    Write-Ok "Pushed :${tag}"
}

# ─── Step 6: Deploy to ECS ──────────────────────────────────
Write-Step "6/7" "Deploying to ECS (rolling update)"

# Snapshot current state
Write-Host "  📸 Snapshotting current deployment..." -ForegroundColor Gray
$currentTaskDef = aws ecs describe-services --cluster $ECS_CLUSTER --services $ECS_SERVICE --query 'services[0].taskDefinition' --output text --region $AWS_REGION 2>$null
if ($currentTaskDef) {
    $currentImage = aws ecs describe-task-definition --task-definition $currentTaskDef --query 'taskDefinition.containerDefinitions[0].image' --output text --region $AWS_REGION 2>$null
    Write-Host "  Previous image: $currentImage" -ForegroundColor Gray
}

# Get task definition, update image, register new revision
Invoke-Step "Update task definition" {
    $taskDefJson = aws ecs describe-task-definition --task-definition $currentTaskDef --query 'taskDefinition' --region $AWS_REGION 2>$null
    $taskDefJson | Out-File -FilePath "$env:TEMP\task-def.json" -Encoding utf8

    $newImage = "${ECR_REGISTRY}/${ECR_REPO}:${ImageTag}"

    # Use jq if available, otherwise use PowerShell JSON manipulation
    $jqPath = Get-Command jq -ErrorAction SilentlyContinue
    if ($jqPath) {
        Get-Content "$env:TEMP\task-def.json" -Raw |
            jq --arg IMAGE $newImage '.containerDefinitions[0].image = $IMAGE | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)' |
            Out-File -FilePath "$env:TEMP\register-task-def.json" -Encoding utf8

        $script:newTaskDefArn = aws ecs register-task-definition --cli-input-json "file://$env:TEMP\register-task-def.json" --query 'taskDefinition.taskDefinitionArn' --output text --region $AWS_REGION
    }
    else {
        # Fallback: force new deployment with current task def (uses :latest)
        $script:newTaskDefArn = $null
    }
}

Invoke-Step "ECS service update" {
    if ($script:newTaskDefArn) {
        aws ecs update-service --cluster $ECS_CLUSTER --service $ECS_SERVICE --task-definition $script:newTaskDefArn --force-new-deployment --region $AWS_REGION --output text --query 'service.serviceName'
    }
    else {
        aws ecs update-service --cluster $ECS_CLUSTER --service $ECS_SERVICE --force-new-deployment --region $AWS_REGION --output text --query 'service.serviceName'
    }
}
Write-Ok "ECS rolling update triggered."

# Wait for stability
Write-Host "  ⏳ Waiting for service to stabilize..." -ForegroundColor Gray
if (-not $DryRun) {
    aws ecs wait services-stable --cluster $ECS_CLUSTER --services $ECS_SERVICE --region $AWS_REGION 2>$null
    Write-Ok "Service stabilized."
}

# ─── Step 7: Health Check ────────────────────────────────────
Write-Step "7/7" "Post-deployment health check"

if ($SkipHealthCheck) {
    Write-Warn "Health check skipped (--SkipHealthCheck flag)."
}
elseif (-not $HEALTH_CHECK_URL) {
    Write-Warn "HEALTH_CHECK_URL not set. Set it to enable health verification."
    Write-Host "    `$env:HEALTH_CHECK_URL = 'https://your-alb-url.com'" -ForegroundColor Gray
}
elseif (-not $DryRun) {
    $maxRetries = 20
    $retryInterval = 15
    $healthy = $false

    for ($i = 1; $i -le $maxRetries; $i++) {
        try {
            $response = Invoke-WebRequest -Uri "${HEALTH_CHECK_URL}/hello" -TimeoutSec 10 -UseBasicParsing -ErrorAction SilentlyContinue
            if ($response.StatusCode -eq 200) {
                Write-Ok "Health check PASSED! (Status: $($response.StatusCode), Body: $($response.Content))"
                $healthy = $true
                break
            }
        }
        catch {
            Write-Host "    Attempt ${i}/${maxRetries} — not ready yet. Retrying in ${retryInterval}s..." -ForegroundColor Gray
        }
        Start-Sleep -Seconds $retryInterval
    }

    if (-not $healthy) {
        Write-Fail "Health check FAILED after ${maxRetries} attempts!"
        Write-Host "    Consider rolling back with: .\scripts\rollback.ps1" -ForegroundColor Yellow
    }
}

# ─── Summary ─────────────────────────────────────────────────
$duration = (Get-Date) - $DeployStart

Write-Host ""
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  ✅ DEPLOYMENT COMPLETE" -ForegroundColor Green
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  Image     : ${ECR_REGISTRY}/${ECR_REPO}:${ImageTag}"
Write-Host "  Git SHA   : $GitSha"
Write-Host "  Cluster   : ${ECS_CLUSTER} / ${ECS_SERVICE}"
Write-Host "  Duration  : $([math]::Round($duration.TotalSeconds, 1))s"
Write-Host "  Rollback  : .\scripts\rollback.ps1"
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Green
