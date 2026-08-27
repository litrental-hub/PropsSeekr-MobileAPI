// ============================================================
// FILE: FileUploadService.cs
// ============================================================
// Single endpoint for file upload — any size.
//
// Flow:
//   1. UI calls POST /upload with { "fileName": "chat.txt" }
//   2. Lambda returns { "uploadUrl": "https://s3...", "bucket": "...", "key": "..." }
//   3. UI uploads file directly to S3 using PUT <uploadUrl>
//
// File never passes through API Gateway — no size limit.
//
// INTEGRATION (in Function.cs):
//
// 1. Add field:
//    private readonly FileUploadService _fileUpload;
//
// 2. Add to constructor:
//    _fileUpload = new FileUploadService(_s3Client);
//
// 3. Add route in FunctionHandler:
//    if (path.EndsWith("/upload"))
//        return await _fileUpload.HandleUploadAsync(request, context);
// ============================================================

using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using System.Text;
using System.Text.Json;

namespace propseekr_file_processor
{
    public class FileUploadService
    {
        private readonly AmazonS3Client _s3Client;
        private readonly string _bucketName;

        public FileUploadService(AmazonS3Client s3Client)
        {
            _s3Client = s3Client;
            _bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME")
                ?? "propseekr-chat-files";
        }

        public async Task<APIGatewayProxyResponse> HandleUploadAsync(
            APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                var rawBody = request.Body ?? "";
                var body = request.IsBase64Encoded
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(rawBody))
                    : rawBody;

                if (string.IsNullOrWhiteSpace(body) || body == "-")
                    return Respond(400, new { error = "Send JSON with fileName" });

                using var jdoc = JsonDocument.Parse(body);
                var root = jdoc.RootElement;

                // Get file name
                string fileName = "chat.txt";
                if (root.TryGetProperty("fileName", out var fnEl) && fnEl.GetString() is string fn)
                    fileName = SanitizeFileName(fn);

                // Build S3 key
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                if (string.IsNullOrEmpty(ext)) ext = ".txt";

                string s3Key = $"uploads/{date}/{nameWithoutExt}_{timestamp}{ext}";

                // Generate presigned PUT URL (valid for 15 minutes)
                var presignRequest = new GetPreSignedUrlRequest
                {
                    BucketName = _bucketName,
                    Key = s3Key,
                    Verb = HttpVerb.PUT,
                    Expires = DateTime.UtcNow.AddMinutes(15),
                    ContentType = "text/plain"
                };

                string presignedUrl = await _s3Client.GetPreSignedURLAsync(presignRequest);

                context.Logger.LogInformation($"Generated presigned URL for s3://{_bucketName}/{s3Key}");

                return Respond(200, new
                {
                    uploadUrl = presignedUrl,
                    bucket = _bucketName,
                    key = s3Key,
                    fileName = fileName,
                    expiresInMinutes = 15
                });
            }
            catch (JsonException ex)
            {
                return Respond(400, new { error = "Invalid JSON", detail = ex.Message });
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Upload error: {ex}");
                return Respond(500, new { error = "Failed to generate upload URL", detail = ex.Message });
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "chat";
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray());
            clean = clean.Replace(' ', '_').Trim('_', '.');
            if (clean.Length > 100) clean = clean[..100];
            return string.IsNullOrEmpty(clean) ? "chat" : clean;
        }

        private static APIGatewayProxyResponse Respond(int status, object body) =>
            new APIGatewayProxyResponse
            {
                StatusCode = status,
                Body = JsonSerializer.Serialize(body, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }),
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "Access-Control-Allow-Origin", "*" },
                    { "Access-Control-Allow-Methods", "POST, OPTIONS" },
                    { "Access-Control-Allow-Headers", "Content-Type" }
                }
            };
    }
}
