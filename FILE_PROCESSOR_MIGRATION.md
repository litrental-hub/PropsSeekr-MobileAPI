# File processor REST migration

The mobile API now exposes the Lambda implementation without rewriting it:

| Lambda route/event | REST route |
| --- | --- |
| `POST /process` | `POST /api/v1/file-processor/process` |
| `POST /embed` | `POST /api/v1/file-processor/embed` |
| `POST /ingest` | `POST /api/v1/file-processor/ingest` |
| `GET /matches` | `GET /api/v1/file-processor/matches` |
| `POST /listing` | `POST /api/v1/file-processor/listing` |
| `POST /upload` | `POST /api/v1/file-processor/upload` |
| Mobile completion callback after S3 PUT | `POST /api/v1/file-processor/pipeline` |

`pipeline` accepts `{ "key": "uploads/.../chat.txt" }` and retains the original sequence: process, ingest, embed listings, embed requirements, and matching. Its bucket is always read from `FileProcessor:S3BucketName` on the server.

## Mobile upload flow

`PropsSeekr_MobileUI/src/api/property.ts` is configured for the no-queue flow:

1. Call `POST /file-processor/upload` through MobileAPI.
2. PUT the selected text file to the returned presigned URL.
3. After S3 confirms the PUT, call `POST /file-processor/pipeline` with the returned `key`. The bucket is never sent to the app.

The pipeline call has a ten-minute client timeout because extraction and embeddings can take longer than ordinary API requests. If the app is closed before step 3, the file remains in S3 but will not be processed; the user can upload again or a later retry/status feature can be added.

## Configuration

`appsettings.json` contains only empty local placeholders for secrets. Do not commit credentials and do not use .NET Secret Manager. Server runtime secrets are loaded from the JSON secret named by `AWS__SecretsManagerConfigName`; ECS supplies AWS credentials through its task role. The secret must be a flat JSON object using the allowed keys in `scripts/DEPLOYMENT_SETUP.md`; `DB_CONNECTION_STRING` is the only database setting. Local development uses the same AWS secret through the developer's AWS profile and `AWS__SecretsManagerConfigName` environment variable.

Embeddings use the configured Google service account with Vertex AI's `gemini-embedding-001` model. `FileProcessor:EmbeddingDimensions` defaults to 1536 to match the current pgvector columns, and `FileProcessor:VertexLocation` defaults to `us-central1`. The legacy file-extraction chat fallback still uses `OPENAI_API_KEY` and `gpt-4o-mini`; ordinary listing and requirement embedding no longer depends on OpenAI.

The endpoints retain the Lambda's anonymous routing for compatibility. Put them behind the same internal gateway or service-to-service authentication before public exposure.
