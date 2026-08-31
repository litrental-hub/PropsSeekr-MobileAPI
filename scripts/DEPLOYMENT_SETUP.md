# Mobile API production deployment setup

The GitHub workflows deploy every successful push to `Main`. They use GitHub
Actions OIDC; do not create or store `AWS_ACCESS_KEY_ID` or
`AWS_SECRET_ACCESS_KEY` in GitHub.

## GitHub environment

Create a `production` environment in the `litrental-hub/PropsSeekr-MobileAPI`
repository and add these environment variables:

| Variable | Value |
| --- | --- |
| `AWS_REGION` | `ap-south-1` |
| `AWS_ACCOUNT_ID` | `307869868474` |
| `ECR_REPOSITORY` | `propseekr-mobile-api` |
| `ECS_CLUSTER` | `default` |
| `ECS_SERVICE` | `propseekr-mobile-api` |
| `ECS_CONTAINER_NAME` | Exact container name in the ECS task definition |
| `HEALTH_CHECK_URL` | Production API base URL, without a trailing slash |
| `AWS_GITHUB_DEPLOY_ROLE_ARN` | ARN of `github-propseekr-mobile-production-deploy` |
| `AWS_SECRETS_MANAGER_CONFIG_NAME` | Name or ARN of the aggregate API runtime JSON secret |

Protect `Main`: require pull requests, review, and passing status checks; block
force pushes and direct developer pushes.

## AWS OIDC role

Create the GitHub OIDC provider with URL
`https://token.actions.githubusercontent.com` and audience `sts.amazonaws.com`.
The production role trust policy must restrict access to this repository and
environment:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": {
      "Federated": "arn:aws:iam::307869868474:oidc-provider/token.actions.githubusercontent.com"
    },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": {
        "token.actions.githubusercontent.com:aud": "sts.amazonaws.com",
        "token.actions.githubusercontent.com:sub": "repo:litrental-hub/PropsSeekr-MobileAPI:environment:production"
      }
    }
  }]
}
```

Grant only ECR push/read for `propseekr-mobile-api`, ECS describe/update and
task-definition registration for the mobile API, plus `iam:PassRole` for the
existing ECS task role and execution role. Do not use `AdministratorAccess`.

## Runtime secrets

Keep database, JWT, payment, OTP, Google service-account, and internal-service
credentials in one AWS Secrets Manager JSON secret. The deployment script puts
only its non-secret name into the task as `AWS__SecretsManagerConfigName`. At
startup, the API reads the secret through the ECS task role; no secret values or
long-lived AWS access keys are injected into the task definition.

The task role needs only `secretsmanager:GetSecretValue` for the configured
secret ARN (and `kms:Decrypt` for its KMS key when a customer-managed key is
used). This permission belongs on the task role, not the task execution role.

Use only this flat key/value schema in the secret. Omit an optional key when
the corresponding feature is disabled; do not add provider metadata, endpoint
URLs, duplicate database fields, nested objects, or arrays.

```json
{
  "DB_CONNECTION_STRING": "Host=...;Port=5432;Database=...;Username=...;Password=...",
  "JWT_KEY": "...",
  "RAZORPAY_KEY_ID": "...",
  "RAZORPAY_KEY_SECRET": "...",
  "RAZORPAY_WEBHOOK_SECRET": "...",
  "MSG91_AUTH_KEY": "...",
  "MSG91_OTP_TEMPLATE_ID": "...",
  "INTERNAL_SERVICE_API_KEY": "...",
  "OPENAI_API_KEY": "...",
  "S3_BUCKET_NAME": "...",
  "GOOGLE_MAPS_API_KEY": "...",
  "GOOGLE_SERVICE_ACCOUNT_TYPE": "service_account",
  "GOOGLE_CLOUD_PROJECT": "...",
  "GOOGLE_PRIVATE_KEY_ID": "...",
  "GOOGLE_PRIVATE_KEY": "-----BEGIN PRIVATE KEY-----\\n...\\n-----END PRIVATE KEY-----\\n",
  "GOOGLE_CLIENT_EMAIL": "...",
  "GOOGLE_CLIENT_ID": "..."
}
```

`DB_CONNECTION_STRING` is the sole database secret; do not store `host`,
`port`, `dbname`, `username`, `password`, `engine`, or `dbInstanceIdentifier`.
`GEMINI_API_KEY`, `GOOGLE_API_KEY`, and the Google OAuth/certificate URL fields
are not used and must not be stored. Vertex authentication needs the listed
Google service-account fields; its endpoint URLs are safe application defaults.
`OPENAI_API_KEY` is needed only for the legacy file-extraction chat fallback.
Rotate any credential that has appeared in source, logs, screenshots, or chat
before production deployment.

For local API execution, authenticate with an AWS CLI/SSO developer profile and
set only `AWS__SecretsManagerConfigName`. Do not use `dotnet user-secrets`.

The React Native app must never call Secrets Manager or contain backend secrets.
It receives business data from the API. The Android Google Maps browser key is a
public client identifier and must be supplied only at build time, restricted to
the Android package name and signing certificate in Google Cloud.

## Operations

`build-and-push-image.sh` creates only `sha-<full-commit-sha>` images.
`deploy-ecs.sh` registers a new task-definition revision and rolls back to the
previous revision if ECS stabilization or the HTTP health check fails.
`rollback-ecs.sh` is available for an explicit operator rollback.

Replace `HEALTH_CHECK_PATH: /hello` in the workflows with `/ready` after the API
has a database-aware readiness endpoint.
