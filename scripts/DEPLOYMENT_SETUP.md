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

Keep database, JWT, payment, OTP, and email credentials in AWS Secrets Manager.
Inject them into the ECS task definition. Rotate any credentials that have been
committed to configuration files before enabling production deployment.

## Operations

`build-and-push-image.sh` creates only `sha-<full-commit-sha>` images.
`deploy-ecs.sh` registers a new task-definition revision and rolls back to the
previous revision if ECS stabilization or the HTTP health check fails.
`rollback-ecs.sh` is available for an explicit operator rollback.

Replace `HEALTH_CHECK_PATH: /hello` in the workflows with `/ready` after the API
has a database-aware readiness endpoint.
