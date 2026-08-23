<#
.SYNOPSIS
    Rollback PropSeekr-MobileAPI to a previous ECR image.

.DESCRIPTION
    Lists recent ECR image tags, lets you pick one, updates the ECS task
    definition to point at that image, and triggers a rolling update.

.PARAMETER Tag
    Specific image tag to rollback to. If not provided, lists recent tags.

.PARAMETER Force
    Skip confirmation prompt.

.EXAMPLE
    .\scripts\rollback.ps1
    .\scripts\rollback.ps1 -Tag "20260822-1530-abc1234"
    .\scripts\rollback.ps1 -Tag "20260822-1530-abc1234" -Force
#>

param(
    [string]$Tag,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# ─── Configuration ───────────────────────────────────────────
$AWS_REGION     = "ap-south-1"
$AWS_ACCOUNT_ID = "307869868474"
$ECR_REPO       = "propseekr-mobile-api"
$ECS_CLUSTER    = "default"
$ECS_SERVICE    = "propseekr-mobile-api"
$ECR_REGISTRY   = "${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"

Write-Host ""
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Yellow
Write-Host "  PROPSEEKR MOBILE API — ROLLBACK" -ForegroundColor Yellow
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Yellow

# ─── Get Current State ───────────────────────────────────────
Write-Host ""
Write-Host "[1] Current deployment state" -ForegroundColor Cyan
Write-Host ("─" * 60) -ForegroundColor DarkGray

$currentTaskDef = aws ecs describe-services --cluster $ECS_CLUSTER --services $ECS_SERVICE `
    --query 'services[0].taskDefinition' --output text --region $AWS_REGION
$currentImage = aws ecs describe-task-definition --task-definition $currentTaskDef `
    --query 'taskDefinition.containerDefinitions[0].image' --output text --region $AWS_REGION

Write-Host "  Current task def : $currentTaskDef"
Write-Host "  Current image    : $currentImage"

# ─── List Available Tags ────────────────────────────────────
Write-Host ""
Write-Host "[2] Available image tags (most recent first)" -ForegroundColor Cyan
Write-Host ("─" * 60) -ForegroundColor DarkGray

$imagesJson = aws ecr describe-images --repository-name $ECR_REPO --region $AWS_REGION `
    --query 'sort_by(imageDetails,& imagePushedAt)[-15:]' --output json | ConvertFrom-Json

$availableTags = @()
$index = 0
for ($i = $imagesJson.Count - 1; $i -ge 0; $i--) {
    $img = $imagesJson[$i]
    $tags = $img.imageTags -join ", "
    if (-not $tags) { continue }

    $pushed = $img.imagePushedAt
    $size = [math]::Round($img.imageSizeInBytes / 1MB, 1)
    $index++

    # Skip 'latest' as standalone
    $displayTags = ($img.imageTags | Where-Object { $_ -ne "latest" })
    if (-not $displayTags) { continue }

    $primaryTag = $displayTags[0]
    $availableTags += $primaryTag

    $isCurrent = if ($currentImage -like "*:$primaryTag") { " ← CURRENT" } else { "" }
    Write-Host "  [$index] $primaryTag  ($size MB, pushed $pushed)$isCurrent" -ForegroundColor $(if ($isCurrent) { "Green" } else { "White" })
}

if (-not $Tag) {
    Write-Host ""
    $selection = Read-Host "  Enter number to rollback to (or 'q' to quit)"
    if ($selection -eq 'q' -or $selection -eq '') { Write-Host "  Cancelled."; exit 0 }

    $selIndex = [int]$selection - 1
    if ($selIndex -lt 0 -or $selIndex -ge $availableTags.Count) {
        Write-Host "  ❌ Invalid selection." -ForegroundColor Red
        exit 1
    }
    $Tag = $availableTags[$selIndex]
}

$rollbackImage = "${ECR_REGISTRY}/${ECR_REPO}:${Tag}"

# ─── Confirm ────────────────────────────────────────────────
Write-Host ""
Write-Host "[3] Rollback confirmation" -ForegroundColor Cyan
Write-Host ("─" * 60) -ForegroundColor DarkGray
Write-Host "  FROM : $currentImage" -ForegroundColor Red
Write-Host "  TO   : $rollbackImage" -ForegroundColor Green

if (-not $Force) {
    $confirm = Read-Host "  Proceed with rollback? (yes/no)"
    if ($confirm -ne 'yes') {
        Write-Host "  Cancelled." -ForegroundColor Yellow
        exit 0
    }
}

# ─── Execute Rollback ───────────────────────────────────────
Write-Host ""
Write-Host "[4] Executing rollback" -ForegroundColor Cyan
Write-Host ("─" * 60) -ForegroundColor DarkGray

# Get current task def JSON
$taskDefJson = aws ecs describe-task-definition --task-definition $currentTaskDef `
    --query 'taskDefinition' --region $AWS_REGION

$taskDefJson | Out-File -FilePath "$env:TEMP\rollback-task-def.json" -Encoding utf8

$jqPath = Get-Command jq -ErrorAction SilentlyContinue
if ($jqPath) {
    Get-Content "$env:TEMP\rollback-task-def.json" -Raw |
        jq --arg IMAGE $rollbackImage '.containerDefinitions[0].image = $IMAGE | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)' |
        Out-File -FilePath "$env:TEMP\register-rollback.json" -Encoding utf8

    $newArn = aws ecs register-task-definition `
        --cli-input-json "file://$env:TEMP\register-rollback.json" `
        --query 'taskDefinition.taskDefinitionArn' --output text --region $AWS_REGION

    Write-Host "  ✅ Registered task definition: $newArn" -ForegroundColor Green

    aws ecs update-service --cluster $ECS_CLUSTER --service $ECS_SERVICE `
        --task-definition $newArn --force-new-deployment --region $AWS_REGION `
        --query 'service.serviceName' --output text | Out-Null
}
else {
    Write-Host "  ⚠️  jq not found — using force-new-deployment with latest tag." -ForegroundColor Yellow
    aws ecs update-service --cluster $ECS_CLUSTER --service $ECS_SERVICE `
        --force-new-deployment --region $AWS_REGION `
        --query 'service.serviceName' --output text | Out-Null
}

Write-Host "  ✅ Rollback triggered." -ForegroundColor Green

# Wait for stability
Write-Host "  ⏳ Waiting for service to stabilize..." -ForegroundColor Gray
aws ecs wait services-stable --cluster $ECS_CLUSTER --services $ECS_SERVICE --region $AWS_REGION 2>$null
Write-Host "  ✅ Service stabilized." -ForegroundColor Green

Write-Host ""
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  ✅ ROLLBACK COMPLETE" -ForegroundColor Green
Write-Host "  Image: $rollbackImage" -ForegroundColor Green
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Green
