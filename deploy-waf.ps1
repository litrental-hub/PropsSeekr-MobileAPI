# AWS CLI PowerShell Deployment Script for PropSeekr AWS WAF WebACL
# Region: ap-south-1
# Usage: .\deploy-waf.ps1 -Region "ap-south-1" -ResourceArn "<ALB_OR_API_GATEWAY_ARN>"

param (
    [string]$Region = "ap-south-1",
    [string]$Environment = "production",
    [string]$ResourceArn = ""
)

Write-Host "=== PropSeekr AWS WAF WebACL Deployment Script ===" -ForegroundColor Cyan
Write-Host "Target Region: $Region"
Write-Host "Target Environment: $Environment"

# 1. JSON Configuration Payload for WebACL Creation
$webAclJson = @"
{
  "Name": "PropSeekr-WebACL-$Environment",
  "Scope": "REGIONAL",
  "DefaultAction": { "Allow": {} },
  "Description": "Edge security and rate-limiting WebACL for PropSeekr-MobileAPI",
  "Rules": [
    {
      "Name": "AWSManagedRulesCommonRuleSet",
      "Priority": 10,
      "Statement": {
        "ManagedRuleGroupStatement": {
          "VendorName": "AWS",
          "Name": "AWSManagedRulesCommonRuleSet"
        }
      },
      "OverrideAction": { "Count": {} },
      "VisibilityConfig": {
        "SampledRequestsEnabled": true,
        "CloudWatchMetricsEnabled": true,
        "MetricName": "AWSManagedRulesCommonRuleSet"
      }
    },
    {
      "Name": "AWSManagedRulesKnownBadInputsRuleSet",
      "Priority": 20,
      "Statement": {
        "ManagedRuleGroupStatement": {
          "VendorName": "AWS",
          "Name": "AWSManagedRulesKnownBadInputsRuleSet"
        }
      },
      "OverrideAction": { "Count": {} },
      "VisibilityConfig": {
        "SampledRequestsEnabled": true,
        "CloudWatchMetricsEnabled": true,
        "MetricName": "AWSManagedRulesKnownBadInputsRuleSet"
      }
    },
    {
      "Name": "AWSManagedRulesSQLiRuleSet",
      "Priority": 30,
      "Statement": {
        "ManagedRuleGroupStatement": {
          "VendorName": "AWS",
          "Name": "AWSManagedRulesSQLiRuleSet"
        }
      },
      "OverrideAction": { "Count": {} },
      "VisibilityConfig": {
        "SampledRequestsEnabled": true,
        "CloudWatchMetricsEnabled": true,
        "MetricName": "AWSManagedRulesSQLiRuleSet"
      }
    },
    {
      "Name": "PropSeekr-Global-RateLimit",
      "Priority": 40,
      "Statement": {
        "RateBasedStatement": {
          "Limit": 2000,
          "AggregateKeyType": "IP"
        }
      },
      "Action": { "Count": {} },
      "VisibilityConfig": {
        "SampledRequestsEnabled": true,
        "CloudWatchMetricsEnabled": true,
        "MetricName": "PropSeekr-Global-RateLimit"
      }
    },
    {
      "Name": "PropSeekr-Auth-RateLimit",
      "Priority": 50,
      "Statement": {
        "RateBasedStatement": {
          "Limit": 100,
          "AggregateKeyType": "IP",
          "ScopeDownStatement": {
            "OrStatement": {
              "Statements": [
                {
                  "ByteMatchStatement": {
                    "SearchString": "/api/v1/auth/login",
                    "FieldToMatch": { "UriPath": {} },
                    "TextTransformations": [ { "Priority": 0, "Type": "LOWERCASE" } ],
                    "PositionalConstraint": "CONTAINS"
                  }
                },
                {
                  "ByteMatchStatement": {
                    "SearchString": "/api/v2/auth/login",
                    "FieldToMatch": { "UriPath": {} },
                    "TextTransformations": [ { "Priority": 0, "Type": "LOWERCASE" } ],
                    "PositionalConstraint": "CONTAINS"
                  }
                },
                {
                  "ByteMatchStatement": {
                    "SearchString": "/api/v1/auth/register",
                    "FieldToMatch": { "UriPath": {} },
                    "TextTransformations": [ { "Priority": 0, "Type": "LOWERCASE" } ],
                    "PositionalConstraint": "CONTAINS"
                  }
                }
              ]
            }
          }
        }
      },
      "Action": { "Count": {} },
      "VisibilityConfig": {
        "SampledRequestsEnabled": true,
        "CloudWatchMetricsEnabled": true,
        "MetricName": "PropSeekr-Auth-RateLimit"
      }
    },
    {
      "Name": "PropSeekr-OTP-RateLimit",
      "Priority": 60,
      "Statement": {
        "RateBasedStatement": {
          "Limit": 30,
          "AggregateKeyType": "IP",
          "ScopeDownStatement": {
            "ByteMatchStatement": {
              "SearchString": "-otp",
              "FieldToMatch": { "UriPath": {} },
              "TextTransformations": [ { "Priority": 0, "Type": "LOWERCASE" } ],
              "PositionalConstraint": "CONTAINS"
            }
          }
        }
      },
      "Action": { "Count": {} },
      "VisibilityConfig": {
        "SampledRequestsEnabled": true,
        "CloudWatchMetricsEnabled": true,
        "MetricName": "PropSeekr-OTP-RateLimit"
      }
    },
    {
      "Name": "PropSeekr-Search-RateLimit",
      "Priority": 70,
      "Statement": {
        "RateBasedStatement": {
          "Limit": 150,
          "AggregateKeyType": "IP",
          "ScopeDownStatement": {
            "OrStatement": {
              "Statements": [
                {
                  "ByteMatchStatement": {
                    "SearchString": "/api/v1/search",
                    "FieldToMatch": { "UriPath": {} },
                    "TextTransformations": [ { "Priority": 0, "Type": "LOWERCASE" } ],
                    "PositionalConstraint": "CONTAINS"
                  }
                },
                {
                  "ByteMatchStatement": {
                    "SearchString": "/api/v1/user-matches",
                    "FieldToMatch": { "UriPath": {} },
                    "TextTransformations": [ { "Priority": 0, "Type": "LOWERCASE" } ],
                    "PositionalConstraint": "CONTAINS"
                  }
                }
              ]
            }
          }
        }
      },
      "Action": { "Count": {} },
      "VisibilityConfig": {
        "SampledRequestsEnabled": true,
        "CloudWatchMetricsEnabled": true,
        "MetricName": "PropSeekr-Search-RateLimit"
      }
    },
    {
      "Name": "PropSeekr-PaymentUpload-RateLimit",
      "Priority": 80,
      "Statement": {
        "RateBasedStatement": {
          "Limit": 50,
          "AggregateKeyType": "IP",
          "ScopeDownStatement": {
            "OrStatement": {
              "Statements": [
                {
                  "ByteMatchStatement": {
                    "SearchString": "/api/v1/payment",
                    "FieldToMatch": { "UriPath": {} },
                    "TextTransformations": [ { "Priority": 0, "Type": "LOWERCASE" } ],
                    "PositionalConstraint": "CONTAINS"
                  }
                },
                {
                  "ByteMatchStatement": {
                    "SearchString": "/upload-photo",
                    "FieldToMatch": { "UriPath": {} },
                    "TextTransformations": [ { "Priority": 0, "Type": "LOWERCASE" } ],
                    "PositionalConstraint": "CONTAINS"
                  }
                }
              ]
            }
          }
        }
      },
      "Action": { "Count": {} },
      "VisibilityConfig": {
        "SampledRequestsEnabled": true,
        "CloudWatchMetricsEnabled": true,
        "MetricName": "PropSeekr-PaymentUpload-RateLimit"
      }
    }
  ],
  "VisibilityConfig": {
    "SampledRequestsEnabled": true,
    "CloudWatchMetricsEnabled": true,
    "MetricName": "PropSeekr-WebACL-$Environment"
  }
}
"@

$webAclFile = [System.IO.Path]::GetTempFileName() + ".json"
[System.IO.File]::WriteAllText($webAclFile, $webAclJson)

Write-Host "Creating WebACL via AWS CLI..." -ForegroundColor Yellow
$createOutput = aws wafv2 create-web-acl --region $Region --cli-input-json file://$webAclFile 2>&1

if ($LASTEXITCODE -eq 0) {
    Write-Host "WebACL created successfully!" -ForegroundColor Green
    Write-Host $createOutput
    
    if (-not [string]::IsNullOrWhiteSpace($ResourceArn)) {
        Write-Host "Associating WebACL with resource $ResourceArn..." -ForegroundColor Yellow
        $webAclArn = ($createOutput | ConvertFrom-Json).Summary.ARN
        aws wafv2 associate-web-acl --web-acl-arn $webAclArn --resource-arn $ResourceArn --region $Region
        Write-Host "Association completed." -ForegroundColor Green
    } else {
        Write-Host "ResourceArn parameter was empty. Skipping WebACL association step." -ForegroundColor Yellow
    }
} else {
    Write-Host "AWS CLI execution status: $createOutput" -ForegroundColor Red
}

Remove-Item -Path $webAclFile -ErrorAction SilentlyContinue
