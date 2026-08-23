<#
.SYNOPSIS
    Health check script for PropSeekr-MobileAPI.

.DESCRIPTION
    Checks the API health endpoint, Swagger UI, and ECS service status.

.PARAMETER Url
    Base URL of the API (e.g. https://api.propseekr.com). 
    Falls back to HEALTH_CHECK_URL env var.

.PARAMETER Detailed
    Show detailed ECS service info.

.EXAMPLE
    .\scripts\health-check.ps1 -Url https://api.propseekr.com
    .\scripts\health-check.ps1 -Detailed
#>

param(
    [string]$Url,
    [switch]$Detailed
)

$ErrorActionPreference = "Stop"

# ─── Configuration ───────────────────────────────────────────
$AWS_REGION   = "ap-south-1"
$ECS_CLUSTER  = "default"
$ECS_SERVICE  = "propseekr-mobile-api"

if (-not $Url) { $Url = $env:HEALTH_CHECK_URL }

Write-Host ""
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  PROPSEEKR MOBILE API — HEALTH CHECK" -ForegroundColor Cyan
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Cyan

# ─── ECS Service Status ─────────────────────────────────────
Write-Host ""
Write-Host "[1] ECS Service Status" -ForegroundColor Cyan
Write-Host ("─" * 60) -ForegroundColor DarkGray

try {
    $serviceJson = aws ecs describe-services --cluster $ECS_CLUSTER --services $ECS_SERVICE `
        --region $AWS_REGION --output json | ConvertFrom-Json

    $svc = $serviceJson.services[0]
    $status = $svc.status
    $running = $svc.runningCount
    $desired = $svc.desiredCount
    $pending = $svc.pendingCount
    $taskDef = $svc.taskDefinition

    $statusColor = if ($status -eq "ACTIVE" -and $running -eq $desired) { "Green" } else { "Yellow" }

    Write-Host "  Status        : $status" -ForegroundColor $statusColor
    Write-Host "  Running Tasks : $running / $desired desired" -ForegroundColor $(if ($running -eq $desired) { "Green" } else { "Red" })
    Write-Host "  Pending Tasks : $pending"
    Write-Host "  Task Def      : $taskDef"

    # Get current image
    $currentImage = aws ecs describe-task-definition --task-definition $taskDef `
        --query 'taskDefinition.containerDefinitions[0].image' --output text --region $AWS_REGION
    Write-Host "  Current Image : $currentImage"

    if ($Detailed) {
        Write-Host ""
        Write-Host "  Latest Events:" -ForegroundColor Gray
        $svc.events | Select-Object -First 5 | ForEach-Object {
            Write-Host "    [$($_.createdAt)] $($_.message)" -ForegroundColor Gray
        }
    }
}
catch {
    Write-Host "  ⚠️  Could not fetch ECS status: $($_.Exception.Message)" -ForegroundColor Yellow
}

# ─── HTTP Health Checks ─────────────────────────────────────
if (-not $Url) {
    Write-Host ""
    Write-Host "[2] HTTP Health Checks — SKIPPED" -ForegroundColor Yellow
    Write-Host "  Set -Url or `$env:HEALTH_CHECK_URL to enable." -ForegroundColor Gray
}
else {
    Write-Host ""
    Write-Host "[2] HTTP Health Checks ($Url)" -ForegroundColor Cyan
    Write-Host ("─" * 60) -ForegroundColor DarkGray

    $endpoints = @(
        @{ Name = "Health (/hello)";          Path = "/hello" },
        @{ Name = "Swagger UI";               Path = "/swagger/index.html" },
        @{ Name = "Swagger JSON";             Path = "/swagger/v1/swagger.json" }
    )

    foreach ($ep in $endpoints) {
        $fullUrl = "${Url}$($ep.Path)"
        try {
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $response = Invoke-WebRequest -Uri $fullUrl -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
            $stopwatch.Stop()

            $statusCode = $response.StatusCode
            $latency = $stopwatch.ElapsedMilliseconds

            if ($statusCode -eq 200) {
                Write-Host "  ✅ $($ep.Name) — ${statusCode} (${latency}ms)" -ForegroundColor Green
                if ($ep.Path -eq "/hello") {
                    Write-Host "     Response: $($response.Content)" -ForegroundColor Gray
                }
            }
            else {
                Write-Host "  ⚠️  $($ep.Name) — ${statusCode} (${latency}ms)" -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host "  ❌ $($ep.Name) — FAILED ($($_.Exception.Message))" -ForegroundColor Red
        }
    }
}

# ─── ECR Image Info ──────────────────────────────────────────
Write-Host ""
Write-Host "[3] Latest ECR Images" -ForegroundColor Cyan
Write-Host ("─" * 60) -ForegroundColor DarkGray

try {
    $imagesJson = aws ecr describe-images --repository-name "propseekr-mobile-api" --region $AWS_REGION `
        --query 'sort_by(imageDetails,& imagePushedAt)[-5:]' --output json | ConvertFrom-Json

    for ($i = $imagesJson.Count - 1; $i -ge 0; $i--) {
        $img = $imagesJson[$i]
        $tags = ($img.imageTags | Where-Object { $_ -ne "latest" }) -join ", "
        if (-not $tags) { continue }
        $size = [math]::Round($img.imageSizeInBytes / 1MB, 1)
        $isLatest = if ($img.imageTags -contains "latest") { " [latest]" } else { "" }
        Write-Host "  $tags  (${size} MB)${isLatest}" -ForegroundColor $(if ($isLatest) { "Green" } else { "White" })
    }
}
catch {
    Write-Host "  ⚠️  Could not fetch ECR images: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Health check complete — $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Cyan
Write-Host "══════════════════════════════════════════════════════════" -ForegroundColor Cyan
