# AWS WAFv2 WebACL Infrastructure-as-Code for PropSeekr-MobileAPI
# Region: ap-south-1
# Scope: REGIONAL (Application Load Balancer / API Gateway Stage)

terraform {
  required_version = ">= 1.5.0"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }
}

provider "aws" {
  region = var.aws_region
}

variable "aws_region" {
  type    = string
  default = "ap-south-1"
}

variable "environment" {
  type    = string
  default = "production"
}

variable "alb_arn" {
  type        = string
  description = "ARN of the Application Load Balancer or API Gateway Stage to attach the WebACL"
  default     = ""
}

# AWS WAFv2 WebACL
resource "aws_wafv2_web_acl" "propseekr_web_acl" {
  name        = "PropSeekr-WebACL-${var.environment}"
  description = "Edge security and rate-limiting WebACL for PropSeekr-MobileAPI"
  scope       = "REGIONAL"

  default_action {
    allow {}
  }

  visibility_config {
    cloudwatch_metrics_enabled = true
    metric_name                = "PropSeekr-WebACL-${var.environment}"
    sampled_requests_enabled   = true
  }

  # Rule 1: AWS Managed Common Rule Set (OWASP Top 10) - COUNT Mode
  rule {
    name     = "AWSManagedRulesCommonRuleSet"
    priority = 10

    override_action {
      count {}
    }

    statement {
      managed_rule_group_statement {
        name        = "AWSManagedRulesCommonRuleSet"
        vendor_name = "AWS"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "AWSManagedRulesCommonRuleSet"
      sampled_requests_enabled   = true
    }
  }

  # Rule 2: AWS Managed Known Bad Inputs Rule Set - COUNT Mode
  rule {
    name     = "AWSManagedRulesKnownBadInputsRuleSet"
    priority = 20

    override_action {
      count {}
    }

    statement {
      managed_rule_group_statement {
        name        = "AWSManagedRulesKnownBadInputsRuleSet"
        vendor_name = "AWS"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "AWSManagedRulesKnownBadInputsRuleSet"
      sampled_requests_enabled   = true
    }
  }

  # Rule 3: AWS Managed SQL Injection Rule Set - COUNT Mode
  rule {
    name     = "AWSManagedRulesSQLiRuleSet"
    priority = 30

    override_action {
      count {}
    }

    statement {
      managed_rule_group_statement {
        name        = "AWSManagedRulesSQLiRuleSet"
        vendor_name = "AWS"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "AWSManagedRulesSQLiRuleSet"
      sampled_requests_enabled   = true
    }
  }

  # Rule 4: Global Baseline Rate Limit (2,000 req / 5 min / IP) - COUNT Mode
  rule {
    name     = "PropSeekr-Global-RateLimit"
    priority = 40

    action {
      count {}
    }

    statement {
      rate_based_statement {
        limit              = 2000
        aggregate_key_type = "IP"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "PropSeekr-Global-RateLimit"
      sampled_requests_enabled   = true
    }
  }

  # Rule 5: Authentication-Sensitive Rate Limit (100 req / 5 min / IP) - COUNT Mode
  # Precisely scoped to /api/v1/auth/login, /api/v2/auth/login, and /api/v1/auth/register
  rule {
    name     = "PropSeekr-Auth-RateLimit"
    priority = 50

    action {
      count {}
    }

    statement {
      rate_based_statement {
        limit              = 100
        aggregate_key_type = "IP"

        scope_down_statement {
          or_statement {
            statement {
              byte_match_statement {
                search_string         = "/api/v1/auth/login"
                positional_constraint = "CONTAINS"
                field_to_match {
                  uri_path {}
                }
                text_transformation {
                  priority = 0
                  type     = "LOWERCASE"
                }
              }
            }
            statement {
              byte_match_statement {
                search_string         = "/api/v2/auth/login"
                positional_constraint = "CONTAINS"
                field_to_match {
                  uri_path {}
                }
                text_transformation {
                  priority = 0
                  type     = "LOWERCASE"
                }
              }
            }
            statement {
              byte_match_statement {
                search_string         = "/api/v1/auth/register"
                positional_constraint = "CONTAINS"
                field_to_match {
                  uri_path {}
                }
                text_transformation {
                  priority = 0
                  type     = "LOWERCASE"
                }
              }
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "PropSeekr-Auth-RateLimit"
      sampled_requests_enabled   = true
    }
  }

  # Rule 6: OTP Endpoint Protection Rate Limit (30 req / 5 min / IP) - COUNT Mode
  # Scoped to /send-email-otp, /verify-email-otp, /send-otp, /verify-otp, /resend-otp
  rule {
    name     = "PropSeekr-OTP-RateLimit"
    priority = 60

    action {
      count {}
    }

    statement {
      rate_based_statement {
        limit              = 30
        aggregate_key_type = "IP"

        scope_down_statement {
          byte_match_statement {
            search_string         = "-otp"
            positional_constraint = "CONTAINS"
            field_to_match {
              uri_path {}
            }
            text_transformation {
              priority = 0
              type     = "LOWERCASE"
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "PropSeekr-OTP-RateLimit"
      sampled_requests_enabled   = true
    }
  }

  # Rule 7: High-Cost Search & Matching Rate Limit (150 req / 5 min / IP) - COUNT Mode
  # Scoped to PostGIS spatial search (/api/v1/search/properties) & user matches calculation (/api/v1/user-matches)
  rule {
    name     = "PropSeekr-Search-RateLimit"
    priority = 70

    action {
      count {}
    }

    statement {
      rate_based_statement {
        limit              = 150
        aggregate_key_type = "IP"

        scope_down_statement {
          or_statement {
            statement {
              byte_match_statement {
                search_string         = "/api/v1/search"
                positional_constraint = "CONTAINS"
                field_to_match {
                  uri_path {}
                }
                text_transformation {
                  priority = 0
                  type     = "LOWERCASE"
                }
              }
            }
            statement {
              byte_match_statement {
                search_string         = "/api/v1/user-matches"
                positional_constraint = "CONTAINS"
                field_to_match {
                  uri_path {}
                }
                text_transformation {
                  priority = 0
                  type     = "LOWERCASE"
                }
              }
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "PropSeekr-Search-RateLimit"
      sampled_requests_enabled   = true
    }
  }

  # Rule 8: High-Cost Payment & File Upload Rate Limit (50 req / 5 min / IP) - COUNT Mode
  # Scoped to /api/v1/payment/order, /api/v1/payment/verify, /api/v1/profile/upload-photo
  rule {
    name     = "PropSeekr-PaymentUpload-RateLimit"
    priority = 80

    action {
      count {}
    }

    statement {
      rate_based_statement {
        limit              = 50
        aggregate_key_type = "IP"

        scope_down_statement {
          or_statement {
            statement {
              byte_match_statement {
                search_string         = "/api/v1/payment"
                positional_constraint = "CONTAINS"
                field_to_match {
                  uri_path {}
                }
                text_transformation {
                  priority = 0
                  type     = "LOWERCASE"
                }
              }
            }
            statement {
              byte_match_statement {
                search_string         = "/upload-photo"
                positional_constraint = "CONTAINS"
                field_to_match {
                  uri_path {}
                }
                text_transformation {
                  priority = 0
                  type     = "LOWERCASE"
                }
              }
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "PropSeekr-PaymentUpload-RateLimit"
      sampled_requests_enabled   = true
    }
  }
}

# WebACL Association with ALB / API Gateway (if alb_arn variable is specified)
resource "aws_wafv2_web_acl_association" "propseekr_alb_assoc" {
  count        = var.alb_arn != "" ? 1 : 0
  resource_arn = var.alb_arn
  web_acl_arn  = aws_wafv2_web_acl.propseekr_web_acl.arn
}

# CloudWatch Alarm for High Counted / Matched Requests (Active during COUNT mode)
resource "aws_cloudwatch_metric_alarm" "waf_high_counted_requests" {
  alarm_name          = "PropSeekr-WAF-HighCountedRequests-${var.environment}"
  comparison_operator = "GreaterThanOrEqualToThreshold"
  evaluation_periods  = 2
  metric_name         = "CountedRequests"
  namespace           = "AWS/WAFV2"
  period              = 300
  statistic           = "Sum"
  threshold           = 1000
  alarm_description   = "Triggers when WAF counted request threshold matches exceed 1,000 in 5 minutes (useful during COUNT evaluation phase)."

  dimensions = {
    WebACL = aws_wafv2_web_acl.propseekr_web_acl.name
    Region = var.aws_region
    Rule   = "ALL"
  }
}

# CloudWatch Alarm for High Block Count (Active after switching to BLOCK mode)
resource "aws_cloudwatch_metric_alarm" "waf_high_block_count" {
  alarm_name          = "PropSeekr-WAF-HighBlockCount-${var.environment}"
  comparison_operator = "GreaterThanOrEqualToThreshold"
  evaluation_periods  = 2
  metric_name         = "BlockedRequests"
  namespace           = "AWS/WAFV2"
  period              = 300
  statistic           = "Sum"
  threshold           = 500
  alarm_description   = "Triggers when WAF blocked request count exceeds 500 in 5 minutes."

  dimensions = {
    WebACL = aws_wafv2_web_acl.propseekr_web_acl.name
    Region = var.aws_region
    Rule   = "ALL"
  }
}

# CloudWatch Alarm for OTP Rate Limit Matches
resource "aws_cloudwatch_metric_alarm" "waf_otp_rate_limit_spike" {
  alarm_name          = "PropSeekr-WAF-OtpSpike-${var.environment}"
  comparison_operator = "GreaterThanOrEqualToThreshold"
  evaluation_periods  = 1
  metric_name         = "PropSeekr-OTP-RateLimit"
  namespace           = "AWS/WAFV2"
  period              = 300
  statistic           = "Sum"
  threshold           = 50
  alarm_description   = "Triggers when WAF OTP rate limit triggers more than 50 times in 5 minutes."

  dimensions = {
    WebACL = aws_wafv2_web_acl.propseekr_web_acl.name
    Region = var.aws_region
    Rule   = "PropSeekr-OTP-RateLimit"
  }
}
