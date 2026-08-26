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

`appsettings.json` contains an intentionally empty `FileProcessor` schema. Do not commit credentials. Set production secrets using environment variables with the original Lambda names (`OPENAI_API_KEY`, `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USERNAME`, `DB_PASSWORD`, `S3_BUCKET_NAME`, and optional Google/Gemini keys), or use the corresponding `FileProcessor:*` user-secret settings locally. Environment variables take precedence.

The supplied Gemini service-account fields are included in the configuration schema for deployment parity. The current Lambda source does not read them; its LLM extraction and embedding calls use `OPENAI_API_KEY`, `gpt-4o-mini`, and `text-embedding-3-small`.

The endpoints retain the Lambda's anonymous routing for compatibility. Put them behind the same internal gateway or service-to-service authentication before public exposure.
