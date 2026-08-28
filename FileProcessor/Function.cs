using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using OpenAI.Chat;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace propseekr_file_processor
{
    public class Function
    {
        private readonly Lazy<ChatClient> _chatClient;
        private readonly VertexAiEmbeddingClient _embeddingClient;
        private readonly AmazonS3Client _s3Client;
        private readonly string? _dbConnectionString;
        private readonly NpgsqlDataSource? _dataSource;
        private readonly IngestService? _ingestService;
        private readonly MatchesApiService? _matchesApi;
        private readonly FileUploadService _fileUpload;
        private readonly ListingFormService? _listingForm;
        public Function()
        {
            _chatClient = new Lazy<ChatClient>(CreateOpenAiChatClient, LazyThreadSafetyMode.ExecutionAndPublication);
            _embeddingClient = VertexAiEmbeddingClient.FromEnvironment();
            _s3Client = new AmazonS3Client();

            /* ERRONEOUS CODE:
            _dbConnectionString = BuildDbConnectionString();
            var dsBuilder = new NpgsqlDataSourceBuilder(_dbConnectionString);
            dsBuilder.UseVector();
            _dataSource = dsBuilder.Build();
            _ingestService = new IngestService(_dbConnectionString, _s3Client);
            _matchesApi = new MatchesApiService(_dbConnectionString);
            _fileUpload = new FileUploadService(_s3Client);
            _listingForm = new ListingFormService(_ingestService);
            */

            string? dbConnectionString = null;
            NpgsqlDataSource? dataSource = null;
            IngestService? ingestService = null;
            MatchesApiService? matchesApi = null;
            ListingFormService? listingForm = null;

            try
            {
                dbConnectionString = BuildDbConnectionString();
                var dsBuilder = new NpgsqlDataSourceBuilder(dbConnectionString);
                dsBuilder.UseVector();
                dataSource = dsBuilder.Build();
                ingestService = new IngestService(dbConnectionString, _s3Client);
                matchesApi = new MatchesApiService(dbConnectionString);
                listingForm = new ListingFormService(
                    ingestService,
                    async context =>
                    {
                        var response = await HandleEmbedAsync(new APIGatewayProxyRequest
                        {
                            Path = "/embed",
                            HttpMethod = "POST",
                            Body = JsonSerializer.Serialize(new { target = "all", batch_size = 4000 }),
                            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
                        }, context);

                        if (response.StatusCode is < 200 or >= 300)
                            throw new InvalidOperationException($"Local embed pipeline returned HTTP {response.StatusCode}.");
                    });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Database initialization bypassed: {ex.Message}");
            }

            _dbConnectionString = dbConnectionString;
            _dataSource = dataSource;
            _ingestService = ingestService;
            _matchesApi = matchesApi;
            _listingForm = listingForm;
            _fileUpload = new FileUploadService(_s3Client);
        }

        private static ChatClient CreateOpenAiChatClient()
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? throw new InvalidOperationException(
                    "OPENAI_API_KEY is required only for the legacy file-extraction chat fallback.");
            return new ChatClient(model: "gpt-4o-mini", apiKey: apiKey);
        }

        private static string BuildDbConnectionString()
        {
            var rawConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(rawConnectionString))
            {
                try
                {
                    return new NpgsqlConnectionStringBuilder(rawConnectionString).ConnectionString;
                }
                catch (Exception ex)
                {
                    var fallbackHost = Environment.GetEnvironmentVariable("DB_HOST");
                    var fallbackPortText = Environment.GetEnvironmentVariable("DB_PORT");
                    var fallbackDatabase = Environment.GetEnvironmentVariable("DB_NAME");
                    var fallbackUsername = Environment.GetEnvironmentVariable("DB_USERNAME");
                    var fallbackPassword = Environment.GetEnvironmentVariable("DB_PASSWORD");

                    if (!string.IsNullOrWhiteSpace(fallbackHost) &&
                        !string.IsNullOrWhiteSpace(fallbackPortText) &&
                        !string.IsNullOrWhiteSpace(fallbackDatabase) &&
                        !string.IsNullOrWhiteSpace(fallbackUsername) &&
                        fallbackPassword != null &&
                        int.TryParse(fallbackPortText, out var fallbackPort))
                    {
                        var fallbackBuilder = new NpgsqlConnectionStringBuilder
                        {
                            Host = fallbackHost,
                            Port = fallbackPort,
                            Database = fallbackDatabase,
                            Username = fallbackUsername,
                            Password = fallbackPassword,
                            Pooling = true
                        };

                        return fallbackBuilder.ConnectionString;
                    }

                    throw new InvalidOperationException(
                        "DB_CONNECTION_STRING is present but invalid. Either fix it or set DB_HOST, DB_PORT, DB_NAME, DB_USERNAME, and DB_PASSWORD.", ex);
                }
            }

            var host = Environment.GetEnvironmentVariable("DB_HOST");
            var portText = Environment.GetEnvironmentVariable("DB_PORT");
            var database = Environment.GetEnvironmentVariable("DB_NAME");
            var username = Environment.GetEnvironmentVariable("DB_USERNAME");
            var password = Environment.GetEnvironmentVariable("DB_PASSWORD");

            if (!string.IsNullOrWhiteSpace(host) &&
                !string.IsNullOrWhiteSpace(portText) &&
                !string.IsNullOrWhiteSpace(database) &&
                !string.IsNullOrWhiteSpace(username) &&
                password != null &&
                int.TryParse(portText, out var port))
            {
                var builder = new NpgsqlConnectionStringBuilder
                {
                    Host = host,
                    Port = port,
                    Database = database,
                    Username = username,
                    Password = password,
                    Pooling = true
                };

                return builder.ConnectionString;
            }

            throw new InvalidOperationException(
                "Set DB_HOST, DB_PORT, DB_NAME, DB_USERNAME, and DB_PASSWORD, or provide DB_CONNECTION_STRING.");
        }

        // ---------------------------------------------
        //  LAMBDA ENTRY POINT
        //  Routes:
        //    POST /process  -> body: {"bucket":"...","key":"..."}
        //    POST /embed    -> body: {"table":"property_listings"} (optional, defaults to property_listings)
        // ---------------------------------------------
        public async Task<APIGatewayProxyResponse> FunctionHandler(
            JsonElement rawInput, ILambdaContext context)
        {
            // â”€â”€ S3 TRIGGER: Check if this is an S3 event â”€â”€
            if (rawInput.TryGetProperty("Records", out var records) &&
                records.GetArrayLength() > 0 &&
                records[0].TryGetProperty("s3", out _))
            {
                try
                {
                    var s3Obj = records[0].GetProperty("s3");
                    var bucket = s3Obj.GetProperty("bucket").GetProperty("name").GetString()!;
                    var key = System.Net.WebUtility.UrlDecode(
                        s3Obj.GetProperty("object").GetProperty("key").GetString()!);

                    context.Logger.LogInformation($"S3 trigger detected: s3://{bucket}/{key}");

                    if (key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        await RunFullPipeline(bucket, key, context);
                        return Respond(200, new { message = "Pipeline complete", bucket, key });
                    }
                    else
                    {
                        context.Logger.LogInformation($"Skipping non-txt file: {key}");
                        return Respond(200, new { message = "Skipped non-txt file", key });
                    }
                }
                catch (Exception ex)
                {
                    context.Logger.LogError($"S3 pipeline error: {ex}");
                    return Respond(500, new { error = "S3 pipeline failed", detail = ex.Message });
                }
            }

            // â”€â”€ API GATEWAY: Deserialize as APIGatewayProxyRequest â”€â”€
            var request = JsonSerializer.Deserialize<APIGatewayProxyRequest>(
                rawInput.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            var path = request.Path?.ToLowerInvariant() ?? "";

            // Route to embed endpoint
            if (path.EndsWith("/embed"))
                return await HandleEmbedAsync(request, context);

            if (path.EndsWith("/ingest"))
            {
                if (_ingestService == null)
                    return Respond(503, new { error = "Ingest service is unavailable because the database is not configured." });
                return await _ingestService.HandleIngestAsync(request, context);
            }

            if (path.EndsWith("/matches"))
            {
                if (_matchesApi == null)
                    return Respond(503, new { error = "Matches API service is unavailable because the database is not configured." });
                return await _matchesApi.HandleGetMatchesAsync(request, context);
            }

            if (path.EndsWith("/listing"))
            {
                if (_listingForm == null)
                    return Respond(503, new { error = "Listing Form service is unavailable because the database is not configured." });
                return await _listingForm.HandleSubmitAsync(request, context);
            }

            if (path.EndsWith("/upload"))
                return await _fileUpload.HandleUploadAsync(request, context);


            // Default: existing S3 process flow
            try
            {
                var body = request.IsBase64Encoded
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(request.Body ?? ""))
                    : request.Body ?? "";

                if (string.IsNullOrWhiteSpace(body))
                {
                    if (rawInput.TryGetProperty("bucket", out _) && rawInput.TryGetProperty("key", out _))
                    {
                        body = rawInput.GetRawText();
                    }
                    else
                    {
                        return Respond(400, new { error = "Send JSON with bucket and key." });
                    }
                }

                using var jdoc = JsonDocument.Parse(body);
                var root = jdoc.RootElement;

                if (!root.TryGetProperty("bucket", out var bEl) ||
                    !root.TryGetProperty("key", out var kEl))
                    return Respond(400, new { error = "Body must have bucket and key." });

                var bucket = bEl.GetString() ?? "";
                var key = kEl.GetString() ?? "";

                /* ERRONEOUS CODE / PREVIOUS CODE:
                context.Logger.LogInformation($"Reading s3://{bucket}/{key}");
                var s3Resp = await _s3Client.GetObjectAsync(
                    new GetObjectRequest { BucketName = bucket, Key = key });
                string rawText;
                using (var reader = new StreamReader(s3Resp.ResponseStream, Encoding.UTF8))
                    rawText = await reader.ReadToEndAsync();
                context.Logger.LogInformation($"Read {rawText.Length} chars from S3");
                var result = await ExtractPropertiesHybridFast(rawText, context);
                var outputKey = key.Replace(".txt", "_listings.json");
                var bytes = Encoding.UTF8.GetBytes(result);
                using var ms = new MemoryStream(bytes);
                await _s3Client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = outputKey,
                    InputStream = ms,
                    ContentType = "application/json"
                });
                context.Logger.LogInformation($"Saved output to s3://{bucket}/{outputKey}");
                */

                string rawText = "";
                var fileName = Path.GetFileName(key);
                string? foundLocalPath = PropertyListingNormalizer.ResolveLocalFilePath(fileName, context.Logger);

                if (foundLocalPath != null)
                {
                    context.Logger.LogInformation($"Reading local file instead of S3: {foundLocalPath}");
                    rawText = await File.ReadAllTextAsync(foundLocalPath, Encoding.UTF8);
                }
                else
                {
                    context.Logger.LogInformation($"Local file not found, downloading from S3: s3://{bucket}/{key}");
                    var s3Resp = await _s3Client.GetObjectAsync(
                        new GetObjectRequest { BucketName = bucket, Key = key });
                    using (var reader = new StreamReader(s3Resp.ResponseStream, Encoding.UTF8))
                        rawText = await reader.ReadToEndAsync();
                }

                context.Logger.LogInformation($"Read {rawText.Length} chars");

                var result = await ExtractPropertiesHybridFast(rawText, fileName, context);

                // Save result JSON
                var outputKey = key.Replace(".txt", "_listings.json");
                var outputFileName = Path.GetFileName(outputKey);
                
                if (foundLocalPath != null)
                {
                    var localOutputPath = Path.Combine(Path.GetDirectoryName(foundLocalPath) ?? "", outputFileName);
                    await File.WriteAllTextAsync(localOutputPath, result, Encoding.UTF8);
                    context.Logger.LogInformation($"Saved output locally to: {localOutputPath}");
                }
                else
                {
                    var bytes = Encoding.UTF8.GetBytes(result);
                    using var ms = new MemoryStream(bytes);
                    await _s3Client.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = bucket,
                        Key = outputKey,
                        InputStream = ms,
                        ContentType = "application/json"
                    });
                    context.Logger.LogInformation($"Saved output to s3://{bucket}/{outputKey}");
                }

                return new APIGatewayProxyResponse
                {
                    StatusCode = 200,
                    Body = result,
                    Headers = new Dictionary<string, string>
                    {
                        { "Content-Type",                "application/json" },
                        { "Access-Control-Allow-Origin", "*"                },
                        { "X-Output-S3-Key",             outputKey          }
                    }
                };
            }
            catch (AmazonS3Exception s3ex)
            {
                context.Logger.LogError($"S3 error: {s3ex.Message}");
                return Respond(500, new { error = "S3 read failed.", detail = s3ex.Message });
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error: {ex}");
                return Respond(500, new { error = "Internal error.", detail = ex.Message });
            }
        }

        private static APIGatewayProxyResponse Respond(int status, object body) =>
            new APIGatewayProxyResponse
            {
                StatusCode = status,
                Body = JsonSerializer.Serialize(body),
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type",                "application/json" },
                    { "Access-Control-Allow-Origin", "*"                }
                }
            };

        // â”€â”€ FULL PIPELINE (triggered by S3 event) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //  Runs: Process â†’ Ingest â†’ Embed Listings â†’ Embed Requirements â†’ Matching SP
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private async Task RunFullPipeline(string bucket, string key, ILambdaContext context)
        {
            // â”€â”€ STEP 1: PROCESS â”€â”€
            context.Logger.LogInformation("Pipeline 1/5: Processing...");
            var processReq = new APIGatewayProxyRequest
            {
                Body = JsonSerializer.Serialize(new { bucket, key }),
                Path = "/process",
                HttpMethod = "POST",
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
            var processResp = await CallInternal(processReq, context);
            if (processResp.StatusCode != 200)
            {
                context.Logger.LogError($"Pipeline FAILED at Process: {processResp.Body}");
                return;
            }
            context.Logger.LogInformation("Pipeline 1/5: Process complete");

            // â”€â”€ STEP 2: INGEST â”€â”€
            if (_ingestService == null || _dataSource == null)
            {
                context.Logger.LogWarning("Pipeline steps 2-5 (Ingest, Embedding, Matching) skipped because the database is not configured.");
                return;
            }

            context.Logger.LogInformation("Pipeline 2/5: Ingesting...");
            var listingsKey = key.Replace(".txt", "_listings.json");
            var ingestReq = new APIGatewayProxyRequest
            {
                Body = JsonSerializer.Serialize(new { bucket, key = listingsKey }),
                Path = "/ingest",
                HttpMethod = "POST",
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
            var ingestResp = await _ingestService.HandleIngestAsync(ingestReq, context);
            if (ingestResp.StatusCode != 200)
            {
                context.Logger.LogError($"Pipeline FAILED at Ingest: {ingestResp.Body}");
                return;
            }
            context.Logger.LogInformation($"Pipeline 2/5: Ingest complete â€” {ingestResp.Body}");

            // â”€â”€ STEP 3: EMBED LISTINGS â”€â”€
            context.Logger.LogInformation("Pipeline 3/5: Embedding listings...");
            var embedListReq = new APIGatewayProxyRequest
            {
                Body = JsonSerializer.Serialize(new { target = "listings", batch_size = 4000 }),
                Path = "/embed",
                HttpMethod = "POST",
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
            var embedListResp = await CallInternal(embedListReq, context);
            context.Logger.LogInformation($"Pipeline 3/5: Listings embedded");

            // â”€â”€ STEP 4: EMBED REQUIREMENTS â”€â”€
            context.Logger.LogInformation("Pipeline 4/5: Embedding requirements...");
            var embedReqReq = new APIGatewayProxyRequest
            {
                Body = JsonSerializer.Serialize(new { target = "requirements", batch_size = 4000 }),
                Path = "/embed",
                HttpMethod = "POST",
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
            var embedReqResp = await CallInternal(embedReqReq, context);
            context.Logger.LogInformation($"Pipeline 4/5: Requirements embedded");

            // Note: Matching SP already runs automatically at the end of HandleEmbedAsync
            // So Step 5 is handled by the embed endpoint itself

            context.Logger.LogInformation($"Pipeline COMPLETE for s3://{bucket}/{key}");
        }

        /// <summary>
        /// Helper to call FunctionHandler internally by converting APIGatewayProxyRequest to JsonElement
        /// </summary>
        private async Task<APIGatewayProxyResponse> CallInternal(
            APIGatewayProxyRequest request, ILambdaContext context)
        {
            var     json = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(request));
            return await FunctionHandler(json, context);
        }

        // â”€â”€ EMBED ENDPOINT â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //  POST /embed
        //  Body: { "target": "listings" | "requirements" | "all", "batch_size": 50 }
        //
        //  Reads rows where embedding IS NULL from listings / requirements tables,
        //  generates Vertex AI Gemini embeddings,
        //  and writes them back using Pgvector.Npgsql.
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private async Task<APIGatewayProxyResponse> HandleEmbedAsync(
            APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                var body = request.IsBase64Encoded
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(request.Body ?? ""))
                    : request.Body ?? "{}";

                string target = "all";
                int batchSize = 50;
                int? listingId = null;
                int? requirementId = null;

                if (!string.IsNullOrWhiteSpace(body) && body.Trim() != "{}")
                {
                    using var jdoc = JsonDocument.Parse(body);
                    var root = jdoc.RootElement;
                    if (root.TryGetProperty("target", out var tEl))
                    {
                        var t = tEl.GetString();
                        if (!string.IsNullOrWhiteSpace(t))
                            target = t.ToLower();
                    }
                    if (root.TryGetProperty("batch_size", out var bsEl) && bsEl.TryGetInt32(out var bs))
                        batchSize = bs;
                    if (root.TryGetProperty("listing_id", out var listingIdEl) && listingIdEl.TryGetInt32(out var parsedListingId))
                        listingId = parsedListingId;
                    if (root.TryGetProperty("requirement_id", out var requirementIdEl) && requirementIdEl.TryGetInt32(out var parsedRequirementId))
                        requirementId = parsedRequirementId;
                }

                if (listingId.HasValue) target = "listings";
                if (requirementId.HasValue) target = "requirements";

                context.Logger.LogInformation(
                    $"Embed job started: target={target}, batch_size={batchSize}");

                var result = new EmbedResult();

                if (target == "listings" || target == "all")
                {
                    var (ok, fail) = await EmbedAllListingsAsync(batchSize, context.Logger, listingId);
                    result.ListingsEmbedded = ok;
                    result.ListingsFailed = fail;
                }

                if (target == "requirements" || target == "all")
                {
                    var (ok, fail) = await EmbedAllRequirementsAsync(batchSize, context.Logger, requirementId);
                    result.RequirementsEmbedded = ok;
                    result.RequirementsFailed = fail;
                }

                context.Logger.LogInformation(
                    $"Embed complete. Listings: {result.ListingsEmbedded} ok / {result.ListingsFailed} failed. " +
                    $"Requirements: {result.RequirementsEmbedded} ok / {result.RequirementsFailed} failed.");

                if (result.ListingsFailed > 0 || result.RequirementsFailed > 0)
                {
                    return Respond(502, new
                    {
                        error = "One or more Gemini embeddings failed.",
                        result.ListingsEmbedded,
                        result.ListingsFailed,
                        result.RequirementsEmbedded,
                        result.RequirementsFailed
                    });
                }


                // Auto-run matching after embeddings complete
                try
                {
                    if (_dataSource == null)
                    {
                        context.Logger.LogWarning("Matching engine skipped because the database is not configured.");
                    }
                    else
                    {
                        await using var matchConn = await _dataSource.OpenConnectionAsync();
                        await using var matchCmd = new NpgsqlCommand(
                            "CALL sp_run_matching_engine(@requirement_id, @listing_id)", matchConn);
                        matchCmd.Parameters.AddWithValue("requirement_id", (object?)requirementId ?? DBNull.Value);
                        matchCmd.Parameters.AddWithValue("listing_id", (object?)listingId ?? DBNull.Value);
                        matchCmd.CommandTimeout = 900;
                        await matchCmd.ExecuteNonQueryAsync();
                        context.Logger.LogInformation("Matching engine completed after embedding");
                    }
                }
                catch (Exception ex2)
                {
                    context.Logger.LogError($"Matching engine error: {ex2.Message}");
                    return Respond(502, new
                    {
                        error = "Matching engine failed after Gemini embeddings were stored.",
                        result.ListingsEmbedded,
                        result.RequirementsEmbedded
                    });
                }


                return Respond(200, result);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Embed error: {ex}");
                return Respond(500, new { error = "Embed failed.", detail = ex.Message });
            }
        }

        // â”€â”€ Embed all listings with NULL embedding â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Gemini embedding requests are made one input at a time while this
        // method retains batch-level database update and fallback behavior.

        private async Task<(int ok, int fail)> EmbedAllListingsAsync(
            int batchSize, ILambdaLogger log, int? listingId = null)
        {
            await using var conn = await OpenDbConnectionAsync();

            // Fetch all listings that need embeddings
            await using var cmd = new NpgsqlCommand(@"
        SELECT listingid,
               property_type,
               listing_type,
               raw_message_text
        FROM   listings
        WHERE  embedding IS NULL
          AND  (@listing_id IS NULL OR listingid = @listing_id)
          AND  raw_message_text IS NOT NULL
          AND  status != 'DELETED'
        ORDER  BY listingid
    ", conn);
            cmd.Parameters.AddWithValue("listing_id", (object?)listingId ?? DBNull.Value);

            var rows = new List<(int id, string text)>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    int id = reader.GetInt32(0);
                    string text = BuildEmbedText(
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)
                    );
                    if (!string.IsNullOrWhiteSpace(text))
                        rows.Add((id, text));
                }
            }

            log.LogInformation($"Found {rows.Count} listings to embed.");
            if (rows.Count == 0) return (0, 0);

            int ok = 0, fail = 0;

            // Process in batches
            for (int i = 0; i < rows.Count; i += batchSize)
            {
                var batch = rows.Skip(i).Take(batchSize).ToList();

                try
                {
                    // Generate embeddings for entire batch in ONE API call
                    var texts = batch.Select(r => r.text).ToList();
                    var vectors = await GenerateEmbeddingsBatchAsync(texts);

                    if (vectors.Count != batch.Count)
                    {
                        log.LogError($"Batch size mismatch: sent {batch.Count}, got {vectors.Count}");
                        fail += batch.Count;
                        continue;
                    }

                    // Update each row with its embedding
                    for (int j = 0; j < batch.Count; j++)
                    {
                        try
                        {
                            await using var update = new NpgsqlCommand(@"
                        UPDATE listings_table
                        SET    embedding  = @vec,
                               embedding_model = @embedding_model,
                               updated_at = NOW()
                        WHERE  listingid = @id
                    ", conn);
                            update.Parameters.AddWithValue("vec", new Pgvector.Vector(vectors[j]));
                            update.Parameters.AddWithValue("embedding_model", _embeddingClient.Model);
                            update.Parameters.AddWithValue("id", batch[j].id);
                            await update.ExecuteNonQueryAsync();
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            log.LogError($"Listing {batch[j].id} update failed: {ex.Message}");
                        }
                    }

                    log.LogInformation(
                        $"Batch {i / batchSize + 1}: embedded {batch.Count} listings " +
                        $"(total: {ok} ok, {fail} fail)");

                    // Small delay between batches to respect rate limits
                    if (i + batchSize < rows.Count)
                        await Task.Delay(200);
                }
                catch (Exception ex)
                {
                    // If entire batch fails, fall back to one-at-a-time for this batch
                    log.LogError($"Batch failed, falling back to individual: {ex.Message}");

                    foreach (var (id, text) in batch)
                    {
                        try
                        {
                            float[] vector = await GenerateEmbeddingAsync(text);

                            await using var update = new NpgsqlCommand(@"
                        UPDATE listings_table
                        SET    embedding  = @vec,
                               embedding_model = @embedding_model,
                               updated_at = NOW()
                        WHERE  listingid = @id
                    ", conn);
                            update.Parameters.AddWithValue("vec", new Pgvector.Vector(vector));
                            update.Parameters.AddWithValue("embedding_model", _embeddingClient.Model);
                            update.Parameters.AddWithValue("id", id);
                            await update.ExecuteNonQueryAsync();

                            ok++;
                            await Task.Delay(100);
                        }
                        catch (Exception innerEx)
                        {
                            fail++;
                            log.LogError($"Listing {id} individual embed failed: {innerEx.Message}");
                        }
                    }
                }
            }

            log.LogInformation($"Listings embedding complete: {ok} ok, {fail} failed");
            return (ok, fail);
        }

        // â”€â”€ Embed all requirements with NULL embedding â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private async Task<(int ok, int fail)> EmbedAllRequirementsAsync(
    int batchSize, ILambdaLogger log, int? requirementId = null)
        {
            await using var conn = await OpenDbConnectionAsync();

            await using var cmd = new NpgsqlCommand(@"
        SELECT requirementid,
               property_type,
               requirement_type,
               raw_message_text
        FROM   requirements
        WHERE  embedding IS NULL
          AND  (@requirement_id IS NULL OR requirementid = @requirement_id)
          AND  raw_message_text IS NOT NULL
          AND  status != 'CLOSED'
        ORDER  BY requirementid
    ", conn);
            cmd.Parameters.AddWithValue("requirement_id", (object?)requirementId ?? DBNull.Value);

            var rows = new List<(int id, string text)>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    int id = reader.GetInt32(0);
                    string text = BuildEmbedText(
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)
                    );
                    if (!string.IsNullOrWhiteSpace(text))
                        rows.Add((id, text));
                }
            }

            log.LogInformation($"Found {rows.Count} requirements to embed.");
            if (rows.Count == 0) return (0, 0);

            int ok = 0, fail = 0;

            // Process in batches
            for (int i = 0; i < rows.Count; i += batchSize)
            {
                var batch = rows.Skip(i).Take(batchSize).ToList();

                try
                {
                    var texts = batch.Select(r => r.text).ToList();
                    var vectors = await GenerateEmbeddingsBatchAsync(texts);

                    if (vectors.Count != batch.Count)
                    {
                        log.LogError($"Batch size mismatch: sent {batch.Count}, got {vectors.Count}");
                        fail += batch.Count;
                        continue;
                    }

                    for (int j = 0; j < batch.Count; j++)
                    {
                        try
                        {
                            await using var update = new NpgsqlCommand(@"
                        UPDATE requirements_table
                        SET    embedding  = @vec,
                               embedding_model = @embedding_model,
                               updated_at = NOW()
                        WHERE  requirementid = @id
                    ", conn);
                            update.Parameters.AddWithValue("vec", new Pgvector.Vector(vectors[j]));
                            update.Parameters.AddWithValue("embedding_model", _embeddingClient.Model);
                            update.Parameters.AddWithValue("id", batch[j].id);
                            await update.ExecuteNonQueryAsync();
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            log.LogError($"Requirement {batch[j].id} update failed: {ex.Message}");
                        }
                    }

                    log.LogInformation(
                        $"Batch {i / batchSize + 1}: embedded {batch.Count} requirements " +
                        $"(total: {ok} ok, {fail} fail)");

                    if (i + batchSize < rows.Count)
                        await Task.Delay(200);
                }
                catch (Exception ex)
                {
                    log.LogError($"Batch failed, falling back to individual: {ex.Message}");

                    foreach (var (id, text) in batch)
                    {
                        try
                        {
                            float[] vector = await GenerateEmbeddingAsync(text);

                            await using var update = new NpgsqlCommand(@"
                        UPDATE requirements_table
                        SET    embedding  = @vec,
                               embedding_model = @embedding_model,
                               updated_at = NOW()
                        WHERE  requirementid = @id
                    ", conn);
                            update.Parameters.AddWithValue("vec", new Pgvector.Vector(vector));
                            update.Parameters.AddWithValue("embedding_model", _embeddingClient.Model);
                            update.Parameters.AddWithValue("id", id);
                            await update.ExecuteNonQueryAsync();

                            ok++;
                            await Task.Delay(100);
                        }
                        catch (Exception innerEx)
                        {
                            fail++;
                            log.LogError($"Requirement {id} individual embed failed: {innerEx.Message}");
                        }
                    }
                }
            }

            log.LogInformation($"Requirements embedding complete: {ok} ok, {fail} failed");
            return (ok, fail);
        }
        // â”€â”€ ADD: New batch embedding method â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private async Task<List<float[]>> GenerateEmbeddingsBatchAsync(List<string> texts)
        {
            if (texts == null || texts.Count == 0)
                return new List<float[]>();

            // Filter out empty texts
            var validTexts = texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (validTexts.Count == 0)
                return new List<float[]>();

            return await _embeddingClient.GenerateEmbeddingsAsync(validTexts);
        }

        // â”€â”€ Builds the text string to embed â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private static string BuildEmbedText(
            string? propertyType, string? recordType, string? rawText)
        {
            var parts = new[]
            {
                propertyType,
                recordType,
                rawText?.Length > 300 ? rawText[..300] : rawText
            };
            return string.Join(" ",
                parts.Where(p => !string.IsNullOrWhiteSpace(p))
            ).ToLower().Trim();
        }

        // â”€â”€ Calls Vertex AI gemini-embedding-001 â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private async Task<float[]> GenerateEmbeddingAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Empty text â€” cannot embed.");

            return await _embeddingClient.GenerateEmbeddingAsync(text);
        }

        // â”€â”€ Opens a pgvector-enabled Npgsql connection â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private async Task<NpgsqlConnection> OpenDbConnectionAsync()
        {
            if (_dataSource == null)
            {
                throw new InvalidOperationException("Database operations are not available because the database connection is not configured.");
            }
            return await _dataSource.OpenConnectionAsync();
        }


        // ---------------------------------------------
        //  MAIN EXTRACTION PIPELINE
        // ---------------------------------------------
        private async Task<string> ExtractPropertiesHybridFast(
            string rawText, string fileName, ILambdaContext? ctx = null)
        {
            var blocks = NormalizeWhatsAppBlocks(SplitWhatsAppMessages(rawText));
            ctx?.Logger.LogInformation($"Parsed {blocks.Count} usable message blocks after hygiene cleanup");

            var expandedBlocks = blocks.SelectMany(SplitSubListings).ToList();

            var candidateBlocks = expandedBlocks
                .Select(b => new { Block = b, CleanedBody = CleanBlock(b.MessageBody) })
                .Where(x => !string.IsNullOrWhiteSpace(x.CleanedBody))
                .Where(x => IsLikelyPropertyMessage(x.CleanedBody))
                .ToList();

            ctx?.Logger.LogInformation($"Found {candidateBlocks.Count} property candidates");

            if (candidateBlocks.Count == 0)
                return JsonSerializer.Serialize(
                    new { listings = new List<PropertyListing>() },
                    new JsonSerializerOptions { WriteIndented = true });

            var localResults = new List<PropertyListing>();
            var llmFallbackBlocks = new List<WhatsAppBlock>();

            foreach (var item in candidateBlocks)
            {
                var listing = TryExtractLocally(item.Block);
                if (listing != null && IsInsertableExtractedListing(listing))
                {
                    listing.GroupName = fileName;
                    listing.MessageDateTime = ParseAndFormatDateTime(item.Block.MessageDate, item.Block.MessageTime);
                    localResults.Add(listing);
                }

                if (ContainsMultiplePropertyRecords(item.CleanedBody) ||
                    listing == null ||
                    !IsHighConfidence(listing))
                {
                    llmFallbackBlocks.Add(item.Block);
                }
            }

            var llmChunks = BuildChunksFromBlocks(llmFallbackBlocks, 12_000);
            var llmResults = await ExtractWithLlmParallel(llmChunks);

            foreach (var listing in llmResults)
            {
                listing.GroupName = fileName;
                if (string.IsNullOrWhiteSpace(listing.MessageDateTime))
                {
                    listing.MessageDateTime = ParseAndFormatDateTime(listing.MessageDate, "");
                }
            }

            var merged = DeduplicateListings(localResults.Concat(llmResults).ToList())
                .Where(IsInsertableExtractedListing)
                .ToList();

            ctx?.Logger.LogInformation(
                $"Extraction output: local={localResults.Count}, llmFallbackBlocks={llmFallbackBlocks.Count}, llmResults={llmResults.Count}, mergedInsertable={merged.Count}");

            return JsonSerializer.Serialize(
                new { listings = merged },
                new JsonSerializerOptions { WriteIndented = true });
        }

        private const string SystemPrompt = """
You are an expert real-estate data extraction engine for WhatsApp group messages from Indore, India.

INPUT FORMAT - each block:
  SenderName: <name>
  MessageDate: <date>
  MessageBody:
  <message text>
  ---

TASK: Extract every real-estate record. Return: {"listings": [...]}

Return only valid JSON for the response body. Do not wrap JSON in markdown fences or extra text.

CRITICAL CLASSIFICATION

recordKind (REQUIRED - one of):
  LISTING_SELL | LISTING_RENT | LISTING_LEASE | REQ_BUY | REQ_RENT | REQ_LEASE | IGNORE

Classification rules:
  - Requirement intent wins FIRST.
    If text asks for property using required, requirement, req, wanted, need, looking for,
    client required, chahiye, lena hai, kharidna -> use REQ_*.
  - "buyer required", "buyer hai", "party wants", "client wants" -> REQ_BUY.
  - Do NOT mark supply listings as requirements just because they say
    "Home Buyers", "Investors", "Preferred Tenant", "Daily Need Shops",
    "Need to rent out", "Renting out", "Need to urgently sell", or "Rent Enquiry".
  - If requirement asks for rent/lease/kiraya -> REQ_RENT or REQ_LEASE.
  - If requirement asks to buy/purchase land/property -> REQ_BUY.
  - Supply/listing/inventory goes to LISTING_*.
  - "available for rent", "for rent", "hotel available for rent" -> LISTING_RENT.
  - "for sale", "sell", "sale", "available", "rate", "demand" -> LISTING_SELL.
  - JV opportunity, joint venture opportunity, investment opportunity, builder opportunity,
    land/project offered for development -> LISTING_SELL unless the message is asking for land/property.
  - IGNORE system notifications, greetings, jobs, ads, media-only messages, and non-property text.

listingType (legacy display field):
  For LISTING_SELL -> Sale
  For LISTING_RENT / LISTING_LEASE -> Rent
  For REQ_* -> Requirement
  For IGNORE -> ""

FIELD RULES

listingType (REQUIRED - one of: Sale | Rent | Requirement | "")
  Requirement FIRST: required, wanted, need, chahiye, lena hai, kharidna, buyer required -> Requirement
  Rent supply: rent, lease, kiraaya, kiraaye, kirae, available for rent -> Rent
  Sale supply: sale, sell, available, rate, project, bechna, sel, bikaau, uplabdh -> Sale
  NOTE: "available for rent" = Rent NOT Sale

propertyType (one of the following EXACT options, categorized as):
  - Residential: Flat / Apartment | Independent House / Bungalow | Villa / Row House | Plot / Land | PG / Hostel | Studio / 1RK | Builder Floor | Farmhouse
  - Commercial: Office Space | Shop / Showroom / Retail | Warehouse / Godown | Factory / Industrial | Hotel / Guest House | Hospital / Clinic | School / College | Petrol Pump / Mall
  - Agricultural: Agricultural Land | Orchard / Fruit Farm | Dairy / Poultry Farm | Farmhouse with Land | Irrigated Land | NA Converted Plot | Plantation Land

configuration: "2BHK", "3BHK", "1RK" - uppercase, no spaces. If multiple are mentioned (e.g. "2, 3 BHK", "2-3 bhk", "2 or 3 BHK"), return them comma-separated (e.g. "2BHK, 3BHK").

location
  - Translate to English. Append "Indore" if absent.
  - STOP immediately at: facing / corner / garden / open / road / ft / ( / size / area / rate / price / contact / bhk / sqft / budget / furnished / RERA / @
  - Location is ONLY the locality/area name. Never include facing direction, road width, plot features.
  - WRONG: "Palakhedi Super Corridor Indore East Facing Corner Garden 40 Ft Road"
  - RIGHT:  "Palakhedi Super Corridor Indore"
  - Max ~50 chars. Locality name only - no project name inside.
  - Common localities: Vijay Nagar, Mahalaxmi Nagar, Super Corridor, Palasia, Nipania,
    Ujjain Road, Palakhedi, Saket, Rau, Khajrana, Scheme 140/78/54, Geeta Bhawan,
    Pithampur, Ring Road, Khandwa Road, MR-10, MR-11, PU4, Bicholi Mardana, Bicholi Hapsi,
    Kanadia Road, Bengali Square, Dewas Naka, Talawali Chanda, TCS Square, Auravindo,
    Bypass Road, AB Road, Dharampuri, Silicon City, Tilak Nagar, Sanwer, Mangaliya,
    Bhawarkua, Navlakha, Annapurna, Pipliyahana, Lasudia, California City, Neemavar Road

projectName: named project; translate Hindi names; do NOT put locality names here

size (number - total sqft/bigha/etc.)
  "25x62=1550 sqft" -> size=1550, width=25, length=62
  "1,50,000 sqft"   -> size=150000  (Indian comma format)
  "600, 800 sqft"   -> size=800     (range - use largest)

sizeUnit: sqft | bigha | gaj | yard | acre

price / pricePerUnit
  - NEVER infer or estimate price. If the original MessageBody has no explicit numeric
    price/rate/rent/budget, set price=null, pricePerUnit=null, priceUnit="".
  - Do not convert size numbers, road widths, floor numbers, phone numbers, deposits,
    or landmark numbers into price.
  4550/- sqft or 4550 per sqft -> pricePerUnit=4550, priceUnit="PerSqFt", price=null
  "13.5 lakh"                  -> price=1350000, priceUnit="Total"
  "3,50,000" / "75,000/-"      -> price=350000 / 75000, priceUnit="Total"
  "rent 20000"                 -> price=20000, priceUnit="PerMonth"
  "90 lakh per bigha"          -> pricePerUnit=9000000, priceUnit="PerBigha"
  "budget 30k" in Requirement  -> price=30000
  Conversions: 1 lakh=100000, 1 crore=10000000, 1k=1000

facing (compass ONLY - exact enum):
  East | West | North | South | East & West | North & South | East & South |
  North & West | North & East | South & West | ""
  Garden/Corner/Front -> NOT facing -> put in roadInfo
  Typos: "best" = West, "wast" = West, "vest" = West

roadInfo: road width, corner, garden facing, near landmark

furnishing: Fully Furnished | Semi Furnished | Unfurnished (default to Unfurnished if not mentioned, never return empty or other values)

contactNumber: ALL 10-digit Indian mobile numbers, comma-separated
contactName: explicit name or use senderName
senderName: Use the WhatsApp header sender. BUT if the message body itself starts with
  "~ Name:" attribution (forwarded message), use THAT name as senderName instead.

rawText: The exact raw text of the message block from the input, unmodified. Do not translate it, do not clean it, do not change it. Keep it exactly as it is in the input.

price / pricePerUnit
  IMPORTANT: "\u20B93300 per sqft" or "rate 3300 sqft" -> pricePerUnit=3300, priceUnit=PerSqFt, price=null
  "\u20B93300 \u0932\u093E\u0916" when context is per-sqft rate -> pricePerUnit=3300, priceUnit=PerSqFt, NOT price=330000000
  Only treat "N \u0932\u093E\u0916 / N lakh" as total price when there is NO size/sqft context suggesting it's a rate.

IGNORE (return no listing):
  System notifications, deleted messages, media omitted, job posts, finance ads, greetings.
  "<This message was edited>" - strip it, do NOT skip the message.

GENERAL:
  1. Translate ALL Hindi/mixed text to English in every field EXCEPT rawText (which must remain in its original language/form unmodified).
  2. Never guess missing values - empty string / null.
  3. One message with 2+ properties -> one listing object per property.
  4. Nothing valid -> {"listings": []}; do not return IGNORE rows unless the schema requires an object.
  5. Return valid JSON only. No markdown, no explanation.

EXAMPLES

INPUT:
SenderName: ~ Ravi
MessageDate: 12/09/24
MessageBody:
Commercial plot for sale near Phoenix Mall, Vijay Nagar
10000 sqft, Rate 13000/- Rs per sqft
Contact: 7772064776
OUTPUT:
{"listings":[{"senderName":"Ravi","messageDate":"12/09/24","listingType":"Sale","propertyType":"Plot","configuration":"","location":"Vijay Nagar Indore","projectName":"","size":[10000],"sizeUnit":"sqft","width":null,"length":null,"price":null,"priceUnit":"PerSqFt","pricePerUnit":13000,"facing":"","roadInfo":"Near Phoenix Mall","furnishing":"","contactName":"Ravi","contactNumber":"7772064776","rawText":"Commercial Plot for Sale near Phoenix Mall, Vijay Nagar\n10000 sqft, Rate 13000 Rs per sqft\nContact: 7772064776"}]}

INPUT:
SenderName: ~ Agent
MessageDate: 14/11/24
MessageBody:
Factory For Sell at Badia Keema, Near BRG Industrial Park, Neemawar Road
Plot Size 40000 sq.ft
Vrinda Estates: Pankaj 9425318240 / Krish 9406653181
OUTPUT:
{"listings":[{"senderName":"Agent","messageDate":"14/11/24","listingType":"Sale","propertyType":"Industrial","configuration":"","location":"Badia Keema Neemavar Road Indore","projectName":"","size":[40000],"sizeUnit":"sqft","width":null,"length":null,"price":null,"priceUnit":"","pricePerUnit":null,"facing":"","roadInfo":"Near BRG Industrial Park","furnishing":"","contactName":"Vrinda Estates","contactNumber":"9425318240, 9406653181","rawText":"Factory for Sale at Badia Keema, near BRG Industrial Park, Neemavar Road\nPlot Size: 40000 sqft\nVrinda Estates - Pankaj: 9425318240, Krish: 9406653181"}]}
""";

        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        //  LLM PARALLEL EXTRACTION
        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500


        private async Task<List<PropertyListing>> ExtractWithLlmParallel(List<string> chunks)
        {
            if (chunks.Count == 0) return new List<PropertyListing>();

            var options = BuildLlmOptions();
            var semaphore = new SemaphoreSlim(8);

            var tasks = chunks.Select(async chunk =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var messages = new List<ChatMessage>
                    {
                        new SystemChatMessage(SystemPrompt),
                        new UserChatMessage(chunk)
                    };

                    var response = await _chatClient.Value.CompleteChatAsync(messages, options);
                    var json = string.Concat(response.Value.Content.Select(part => part.Text));

                    if (!TryParseLlmListings(json, out var list))
                    {
                        Console.WriteLine("LLM chunk returned invalid JSON or a missing listings array.");
                        return new List<PropertyListing>();
                    }

                    return list;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LLM chunk failed: {ex.Message}");
                    return new List<PropertyListing>();
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks);
            return results.SelectMany(x => x).ToList();
        }

        private static bool TryParseLlmListings(string? responseText, out List<PropertyListing> listings)
        {
            listings = new List<PropertyListing>();
            if (string.IsNullOrWhiteSpace(responseText)) return false;
            var json = responseText.Trim();
            json = Regex.Replace(json, @"^\s*```(?:json)?\s*", "", RegexOptions.IgnoreCase);
            json = Regex.Replace(json, @"\s*```\s*$", "", RegexOptions.IgnoreCase);

            // Find first JSON opening char ('{' or '[') and corresponding last closing char
            int idxCurly = json.IndexOf('{');
            int idxBracket = json.IndexOf('[');
            int firstIdx = -1; char closeChar = '\0';
            if (idxCurly >= 0 && (idxBracket == -1 || idxCurly < idxBracket))
            {
                firstIdx = idxCurly; closeChar = '}';
            }
            else if (idxBracket >= 0)
            {
                firstIdx = idxBracket; closeChar = ']';
            }

            if (firstIdx >= 0)
            {
                var lastIdx = json.LastIndexOf(closeChar);
                if (lastIdx > firstIdx)
                    json = json.Substring(firstIdx, lastIdx - firstIdx + 1);
            }

            try
            {
                using var doc = JsonDocument.Parse(json);

                    JsonElement arrElement;
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("listings", out var prop) &&
                        prop.ValueKind == JsonValueKind.Array)
                    {
                        arrElement = prop;
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        arrElement = doc.RootElement;
                    }
                    else
                    {
                        Console.WriteLine("LLM response JSON did not contain a 'listings' array or top-level array.");
                        return false;
                    }

                    listings = ParseListingsFromArray(arrElement);
                    return listings.Count > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to parse LLM JSON: {ex.Message}");
                return false;
            }
        }

        private static List<PropertyListing> ParseListingsFromArray(JsonElement arr)
        {
            var list = new List<PropertyListing>();
            if (arr.ValueKind != JsonValueKind.Array) return list;

            foreach (var item in arr.EnumerateArray())
            {
                list.Add(new PropertyListing
                {
                    RecordKind = PropertyListingNormalizer.NormalizeRecordKind(
                        GetJsonString(item, "recordKind"),
                        GetJsonString(item, "listingType"),
                        GetJsonString(item, "rawText")),
                    SenderName = GetJsonString(item, "senderName"),
                    MessageDate = GetJsonString(item, "messageDate"),
                    MessageDateTime = GetJsonString(item, "messageDateTime"),
                    ListingType = NormalizeListingType(GetJsonString(item, "listingType")),
                    PropertyType = GetJsonString(item, "propertyType"),
                    Configuration = GetJsonString(item, "configuration"),
                    Location = GetJsonString(item, "location"),
                    ProjectName = GetJsonString(item, "projectName"),
                    Size = GetJsonDecimalList(item, "size"),
                    SizeUnit = GetJsonString(item, "sizeUnit"),
                    Width = GetJsonDecimal(item, "width"),
                    Length = GetJsonDecimal(item, "length"),
                    Price = GetJsonDecimal(item, "price"),
                    PriceUnit = GetJsonString(item, "priceUnit"),
                    PricePerUnit = GetJsonDecimal(item, "pricePerUnit"),
                    Facing = NormalizeFacing(GetJsonString(item, "facing")),
                    RoadInfo = GetJsonString(item, "roadInfo"),
                    Furnishing = NormalizeFurnishing(GetJsonString(item, "furnishing")),
                    ContactName = GetJsonString(item, "contactName"),
                    ContactNumber = GetJsonString(item, "contactNumber"),
                    RawText = GetJsonString(item, "rawText")
                }.NormalizeCanonicalFields());
            }

            return list;
        }

        private static ChatCompletionOptions BuildLlmOptions() => new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "property_listings",
                BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        listings = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    senderName = new { type = "string" },
                                    messageDate = new { type = "string" },
                                    messageDateTime = new { type = "string" },
                                    recordKind = new
                                    {
                                        type = "string",
                                        @enum = new[] {
                                            "LISTING_SELL","LISTING_RENT","LISTING_LEASE",
                                            "REQ_BUY","REQ_RENT","REQ_LEASE","IGNORE","" }
                                    },
                                    listingType = new { type = "string", @enum = new[] { "Sale", "Rent", "Requirement", "" } },
                                    propertyType = new
                                    {
                                        type = "string",
                                        @enum = new[] {
                                            "Flat / Apartment", "Independent House / Bungalow", "Villa / Row House", "Plot / Land",
                                            "PG / Hostel", "Studio / 1RK", "Builder Floor", "Farmhouse",
                                            "Office Space", "Shop / Showroom / Retail", "Warehouse / Godown", "Factory / Industrial",
                                            "Hotel / Guest House", "Hospital / Clinic", "School / College", "Petrol Pump / Mall",
                                            "Agricultural Land", "Orchard / Fruit Farm", "Dairy / Poultry Farm", "Farmhouse with Land",
                                            "Irrigated Land", "NA Converted Plot", "Plantation Land", "" }
                                    },
                                    configuration = new { type = "string" },
                                    location = new { type = "string" },
                                    projectName = new { type = "string" },
                                    size = new { type = "array", items = new { type = "number" } },
                                    sizeUnit = new { type = "string", @enum = new[] { "sqft", "bigha", "gaj", "yard", "acre", "" } },
                                    width = new { type = "number" },
                                    length = new { type = "number" },
                                    price = new { type = "number" },
                                    priceUnit = new { type = "string", @enum = new[] { "Total", "PerSqFt", "PerMonth", "PerBigha", "PerAcre", "" } },
                                    pricePerUnit = new { type = "number" },
                                    facing = new
                                    {
                                        type = "string",
                                        @enum = new[] {
                                        "East","West","North","South",
                                        "East & West","North & South","East & South",
                                        "North & West","North & East","South & West","" }
                                    },
                                    roadInfo = new { type = "string" },
                                    // Backup old furnishing enum: new[] { "Fully Furnished", "Semi Furnished", "Furnished", "Unfurnished", "" }
                                    furnishing = new { type = "string", @enum = new[] { "Fully Furnished", "Semi Furnished", "Unfurnished" } },
                                    contactName = new { type = "string" },
                                    contactNumber = new { type = "string" },
                                    rawText = new { type = "string" }
                                },
                                required = Array.Empty<string>(),
                                additionalProperties = false
                            }
                        }
                    },
                    required = new[] { "listings" },
                    additionalProperties = false
                }))
        };

        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        //  MESSAGE PARSING
        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

        private static List<WhatsAppBlock> SplitWhatsAppMessages(string raw)
        {
            raw = StripInvisibleChars(raw);
            var lines = raw.Replace("\r\n", "\n").Split('\n');
            var messages = new List<WhatsAppBlock>();
            WhatsAppBlock? current = null;
            var bodySb = new StringBuilder();

            const string timePattern =
                @"(?<time>\d{1,2}:\d{2}(?::\d{2})?(?:\s|\u202F|\u00A0|[^\x00-\x7F])*?(?:AM|PM|am|pm)?)";
            var bracketPat = new Regex(
                @"^\s*\[(?<date>\d{1,2}/\d{1,2}/\d{2,4}),\s*" + timePattern + @"\]\s*(?<sender>[^:]+):\s*(?<text>.*)$");
            var dashPat = new Regex(
                @"^\s*(?<date>\d{1,2}/\d{1,2}/\d{2,4}),\s*" + timePattern + @"\s*-\s*(?<sender>[^:]+):\s*(?<text>.*)$");

            /* PREVIOUS CODE / BACKUP:
            foreach (var rawLine in lines)
            {
                var line = rawLine ?? string.Empty;
                var m = bracketPat.Match(line);
                if (!m.Success) m = dashPat.Match(line);

                if (m.Success)
                {
                    if (current != null)
                    {
                        current.MessageBody = bodySb.ToString().Trim();
                        messages.Add(current);
                        bodySb.Clear();
                    }
                    current = new WhatsAppBlock
                    {
                        MessageDate = m.Groups["date"].Value.Trim(),
                        MessageTime = m.Groups["time"].Value.Trim(),
                        SenderName = CleanSenderName(m.Groups["sender"].Value.Trim()),
                        RawBlock = line + "\n"
                    };
                    bodySb.Append(m.Groups["text"].Value.Trim());
                }
                else if (current != null)
                {
                    if (bodySb.Length > 0) bodySb.AppendLine();
                    bodySb.Append(line.Trim());
                    current.RawBlock += line + "\n";
                }
            }
            */

            foreach (var rawLine in lines)
            {
                var line = rawLine ?? string.Empty;
                var m = bracketPat.Match(line);
                if (!m.Success) m = dashPat.Match(line);

                if (m.Success)
                {
                    if (current != null)
                    {
                        current.MessageBody = bodySb.ToString().Trim();
                        messages.Add(current);
                        bodySb.Clear();
                    }
                    current = new WhatsAppBlock
                    {
                        MessageDate = m.Groups["date"].Value.Trim(),
                        MessageTime = m.Groups["time"].Value.Trim(),
                        SenderName = CleanSenderName(m.Groups["sender"].Value.Trim()),
                        RawBlock = line + "\n"
                    };
                    bodySb.Append(m.Groups["text"].Value.Trim());
                }
                else
                {
                    if (current == null)
                    {
                        current = new WhatsAppBlock
                        {
                            MessageDate = "",
                            MessageTime = "",
                            SenderName = "Unknown",
                            RawBlock = ""
                        };
                    }
                    if (bodySb.Length > 0) bodySb.AppendLine();
                    bodySb.Append(line.Trim());
                    current.RawBlock += line + "\n";
                }
            }

            if (current != null)
            {
                current.MessageBody = bodySb.ToString().Trim();
                messages.Add(current);
            }

            return messages;
        }

        private static List<WhatsAppBlock> NormalizeWhatsAppBlocks(List<WhatsAppBlock> blocks)
        {
            var cleaned = new List<WhatsAppBlock>();

            foreach (var block in blocks)
            {
                var body = CleanBlock(block.MessageBody);
                if (string.IsNullOrWhiteSpace(body)) continue;
                if (IsNonPropertyNoise(body)) continue;

                var current = new WhatsAppBlock
                {
                    SenderName = block.SenderName,
                    MessageDate = block.MessageDate,
                    MessageTime = block.MessageTime,
                    MessageBody = body,
                    RawBlock = block.RawBlock
                };

                if (IsWeakFragment(body))
                {
                    var previous = cleaned.LastOrDefault();
                    if (previous != null &&
                        SameWhatsAppMoment(previous, current) &&
                        (ShouldAttachFragment(previous.MessageBody, current.MessageBody) ||
                         IsSupplementalFragment(current.MessageBody)))
                    {
                        previous.MessageBody = $"{previous.MessageBody}\n{current.MessageBody}".Trim();
                        previous.RawBlock += current.RawBlock;
                        continue;
                    }
                }

                cleaned.Add(current);
            }

            return cleaned;
        }

        private static bool SameWhatsAppMoment(WhatsAppBlock a, WhatsAppBlock b)
        {
            return string.Equals(a.SenderName, b.SenderName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.MessageDate, b.MessageDate, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.MessageTime, b.MessageTime, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldAttachFragment(string previousBody, string fragmentBody)
        {
            var prevListing = TryExtractLocally(new WhatsAppBlock { MessageBody = previousBody });
            var fragListing = TryExtractLocally(new WhatsAppBlock { MessageBody = fragmentBody });

            if (prevListing == null || fragListing == null) return false;

            bool previousMissingLocation = string.IsNullOrWhiteSpace(prevListing.Location);
            bool fragmentHasLocation = !string.IsNullOrWhiteSpace(fragListing.Location);
            bool previousMissingPrice = !prevListing.Price.HasValue && !prevListing.PricePerUnit.HasValue;
            bool fragmentHasPrice = fragListing.Price.HasValue || fragListing.PricePerUnit.HasValue;
            bool previousMissingConfig = string.IsNullOrWhiteSpace(prevListing.Configuration);
            bool fragmentHasConfig = !string.IsNullOrWhiteSpace(fragListing.Configuration);

            return (previousMissingLocation && fragmentHasLocation)
                || (previousMissingPrice && fragmentHasPrice)
                || (previousMissingConfig && fragmentHasConfig);
        }

        private static bool IsSupplementalFragment(string body)
        {
            var lower = NormalizeText(body).ToLowerInvariant();
            return Regex.IsMatch(lower,
                @"\b(location|near|nagar|square|road|colony|scheme|rent|asking|budget|price|furnished|unfurnished|semi|family|bachelor|\d+(?:\.\d+)?\s*k)\b",
                RegexOptions.IgnoreCase);
        }

        private static bool IsWeakFragment(string body)
        {
            var listing = TryExtractLocally(new WhatsAppBlock { MessageBody = body });
            if (listing == null) return true;

            int facts = 0;
            if (!string.IsNullOrWhiteSpace(listing.PropertyType)) facts++;
            if (!string.IsNullOrWhiteSpace(listing.Location)) facts++;
            if (!string.IsNullOrWhiteSpace(listing.Configuration)) facts++;
            if (listing.Size?.Count > 0) facts++;
            if (listing.Price.HasValue || listing.PricePerUnit.HasValue) facts++;

            return facts <= 1;
        }

        private static bool IsNonPropertyNoise(string body)
        {
            return body.Contains("<Media omitted>", StringComparison.OrdinalIgnoreCase)
                || body.Contains("message was deleted", StringComparison.OrdinalIgnoreCase)
                || body.Contains("follow this link", StringComparison.OrdinalIgnoreCase)
                || body.Contains("join my WhatsApp", StringComparison.OrdinalIgnoreCase)
                || body.Contains("end-to-end encrypted", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(body, @"^\s*(brokerage|only family|deposit|contact for more details)\s*$",
                    RegexOptions.IgnoreCase);
        }

        // Splits one WhatsApp message into per-sub-listing blocks when numbered markers exist
        private static IEnumerable<WhatsAppBlock> SplitSubListings(WhatsAppBlock block)
        {
            var body = block.MessageBody;

            var markerPat = new Regex(
                @"(?:^|\n)\s*(?:\*\s*[1-9]\d?\s*[.\-\:]\s*\*|(?<=[^\d]|^)[1-9]\d?\s*[.\-\:](?=\s))",
                RegexOptions.Multiline);

            var matches = markerPat.Matches(body);
            if (matches.Count < 2)
            {
                markerPat = new Regex(
                    @"(?im)^\s*(?:[^\w\r\n]{0,6}\s*)?(?:\d+(?:\.\d+)?\s*BHK|[1-9]\s*RK|plot|flat|house|duplex|bungalow|villa|office|shop|showroom|hotel|land)\b.{0,80}$",
                    RegexOptions.Multiline);
                matches = markerPat.Matches(body);
            }

            if (matches.Count < 2) { yield return block; yield break; }

            var positions = matches.Cast<Match>().Select(m => m.Index).Append(body.Length).ToList();

            for (int i = 0; i < positions.Count - 1; i++)
            {
                var subBody = body.Substring(positions[i], positions[i + 1] - positions[i]).Trim();
                if (string.IsNullOrWhiteSpace(subBody)) continue;
                yield return new WhatsAppBlock
                {
                    SenderName = block.SenderName,
                    MessageDate = block.MessageDate,
                    MessageTime = block.MessageTime,
                    MessageBody = subBody,
                    RawBlock = block.RawBlock
                };
            }
        }

        private static string CleanBlock(string block)
        {
            if (string.IsNullOrWhiteSpace(block)) return string.Empty;

            var sb = new StringBuilder();
            foreach (var raw in block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.Contains("end-to-end encrypted", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("joined using", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("created this group", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("changed the group", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("changed this group's", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("message was deleted", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("image omitted", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("video omitted", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("document omitted", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("audio omitted", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("sticker omitted", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("disappearing messages", StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.IsMatch(line, @"^\[?\d{1,2}/\d{1,2}/\d{2,4},")) continue;
                if (Regex.IsMatch(line, @"^~?\s*\S+ added \S+", RegexOptions.IgnoreCase)) continue;

                // Strip "This message was edited" suffix
                line = Regex.Replace(line, @"\s*<This message was edited>\s*$", "", RegexOptions.IgnoreCase).Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                sb.AppendLine(line);
            }

            return sb.ToString().Trim();
        }

        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        //  LOCAL EXTRACTION PIPELINE
        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

        private static bool IsLikelyPropertyMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string lower = NormalizeText(text).ToLowerInvariant();

            string[] propertyWords =
            {
                "plot","flat","duplex","house","home","office","shop","showroom","land","site",
                "villa","hotel","godown","warehouse","building","commercial","residential",
                "apartment","farm house","farmhouse","bungalow","row house","rowhouse",
                "independent house","penthouse","studio","agricultural","industrial","kothi"
            };
            string[] intentWords =
            {
                "sale","sell","for sale","rent","rental","lease","required","requirement",
                "wanted","buy","purchase","available","urgent","bechna","bikau","chahiye"
            };
            string[] detailWords =
            {
                "bhk","sqft","sqyard","plot size","area","size","price","rate","demand",
                "budget","location","furnished","lakh","lac","crore","cr","bigha","acre",
                "project","per sqft"
            };

            bool hasHindiProp = text.Contains("\u092A\u094D\u0932\u0949\u091F", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u092A\u094D\u0932\u093E\u091F", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u092B\u094D\u0932\u0948\u091F", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u091C\u092E\u0940\u0928", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u092E\u0915\u093E\u0928", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0926\u0941\u0915\u093E\u0928", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u092D\u0942\u0916\u0902\u0921", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u092C\u0902\u0917\u0932\u093E", StringComparison.OrdinalIgnoreCase);

            bool hasHindiIntent = text.Contains("\u0938\u0947\u0932", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u091A\u093E\u0939\u093F\u090F", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0932\u0947\u0928\u093E \u0939\u0948", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0915\u093F\u0930\u093E\u092F\u093E", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0915\u093F\u0930\u093E\u092F\u0947", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0915\u093F\u0930\u093E\u090F", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u092C\u0947\u091A\u0928\u093E", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u092C\u093F\u0915\u094D\u0930\u0940", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u092C\u093F\u0915\u093E\u090A", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0909\u092A\u0932\u092C\u094D\u0927", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0916\u0930\u0940\u0926\u0928\u093E", StringComparison.OrdinalIgnoreCase);

            bool hasHindiDetail = text.Contains("\u092A\u094D\u0930\u094B\u091C\u0947\u0915\u094D\u091F", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0930\u0947\u091F", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0938\u094D\u0915\u094D\u0935\u093E\u092F\u0930 \u092B\u0940\u091F", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0935\u0930\u094D\u0917 \u092B\u0940\u091F", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0932\u093E\u0916", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u0915\u0930\u094B\u0921\u093C", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u092C\u0940\u0918\u093E", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("\u090F\u0915\u0921\u093C", StringComparison.OrdinalIgnoreCase);

            bool hasProperty = propertyWords.Any(lower.Contains) || hasHindiProp;
            if (!hasProperty && Regex.IsMatch(lower, @"\b\d+\s*(bhk|rk)\b", RegexOptions.IgnoreCase))
                hasProperty = true;
            bool hasIntent = intentWords.Any(lower.Contains) || hasHindiIntent;
            bool hasDetail = detailWords.Any(lower.Contains) || hasHindiDetail;
            bool hasDigit = lower.Any(char.IsDigit);

            bool isExplicitPropertyIntent = hasProperty && hasIntent;
            return hasProperty && (hasDigit || isExplicitPropertyIntent) && (hasIntent || hasDetail);
        }

        private static bool ContainsMultiplePropertyRecords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var lower = NormalizeText(text).ToLowerInvariant();
            var recordStarts = Regex.Matches(lower,
                @"(?im)^\s*\*?\s*(?:plot|house|flat|duplex|bungalow|villa|office|shop|showroom|hotel|land)\s+(?:sell|sale|rent|lease)\b").Count;
            var priceLines = Regex.Matches(lower,
                @"(?im)^\s*(?:sale\s+price|asking\s+price|price|rate)\s*[:\-]").Count;
            var configStarts = Regex.Matches(lower,
                @"(?im)^\s*\d+\s*bhk\s+(?:flat|house|duplex|bungalow|villa)?\s*(?:sell|sale|rent)?\b").Count;

            return recordStarts >= 2
                || priceLines >= 2
                || (recordStarts + configStarts) >= 2;
        }

        private static PropertyListing? TryExtractLocally(WhatsAppBlock block)
        {
            var normalized = NormalizeText(block.MessageBody);

            // If the body itself starts with a name attribution line like "~ Arvendra Lodhi:"
            // (happens when a broker forwards another broker's content), use that name instead.
            var realSender = ExtractBodySender(block.MessageBody) ?? block.SenderName;

            var listing = new PropertyListing
            {
                SenderName = realSender,
                MessageDate = block.MessageDate,
                ListingType = ExtractListingType(block.MessageBody),
                PropertyType = ExtractPropertyType(normalized),
                Configuration = ExtractConfiguration(normalized),
                Location = ExtractLocation(normalized),
                ProjectName = ExtractProjectName(normalized),
                Size = ExtractAllSizes(normalized),
                SizeUnit = ExtractSizeUnit(normalized),
                Width = ExtractWidth(normalized),
                Length = ExtractLength(normalized),
                Price = ExtractPrice(block.MessageBody),
                PriceUnit = ExtractPriceUnit(block.MessageBody),
                PricePerUnit = ExtractPricePerUnit(block.MessageBody),
                Facing = NormalizeFacing(ExtractFacing(normalized)),
                RoadInfo = ExtractRoadInfo(normalized),
                Furnishing = NormalizeFurnishing(ExtractFurnishing(normalized)),
                ContactName = realSender,
                ContactNumber = string.Join(", ", ExtractAllPhones(block.MessageBody)),
                RawText = block.MessageBody
            };

            listing.RecordKind = PropertyListingNormalizer.NormalizeRecordKind(
                listing.RecordKind, listing.ListingType, block.MessageBody);
            return listing.NormalizeCanonicalFields();
        }

        /// <summary>
        /// If the message body contains a sender attribution line at/near the top
        /// (e.g. "~ Arvendra Lodhi:" or "[forwarded] Arvendra Lodhi"), return that name.
        /// </summary>
        private static string? ExtractBodySender(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            // Match "~ Name Name:" ONLY at the very start of the body (forwarded attribution line).
            // Must NOT use Multiline \u2014 we only want position 0, not mid-body "Location : ..." lines.
            var m = Regex.Match(body.TrimStart(),
                @"^~\s*([A-Za-z][A-Za-z\s]{2,40})\s*:");
            if (m.Success)
            {
                var candidate = m.Groups[1].Value.Trim();
                // Reject if it contains property/listing keywords \u2014 it's message content, not a name
                if (!Regex.IsMatch(candidate,
                        @"\b(http|www|omitted|added|created|changed|deleted|plot|flat|sale|rent|" +
                        @"location|rate|size|price|contact|project|property|land|house|shop)\b",
                        RegexOptions.IgnoreCase))
                    return candidate;
            }
            return null;
        }

        private static bool IsHighConfidence(PropertyListing listing)
        {
            int score = 0;
            if (!string.IsNullOrWhiteSpace(listing.ListingType)) score++;
            if (!string.IsNullOrWhiteSpace(listing.PropertyType)) score++;
            if (!string.IsNullOrWhiteSpace(listing.Location)) score++;
            if (!string.IsNullOrWhiteSpace(listing.ProjectName)) score++;
            if (listing.Size?.Count > 0) score++;
            if (listing.Price.HasValue || listing.PricePerUnit.HasValue) score++;
            if (!string.IsNullOrWhiteSpace(listing.ContactNumber)) score++;

            // Force LLM fallback: plot with rate but no location context
            bool suspicious =
                listing.PropertyType == "Plot" &&
                listing.PricePerUnit.HasValue &&
                string.IsNullOrWhiteSpace(listing.Location) &&
                string.IsNullOrWhiteSpace(listing.ProjectName);

            return !suspicious && score >= 5;
        }

        private static bool IsInsertableExtractedListing(PropertyListing listing)
        {
            if (string.IsNullOrWhiteSpace(listing.ListingType)) return false;
            if (string.IsNullOrWhiteSpace(listing.PropertyType)) return false;

            var hasMarketFact = listing.Price.HasValue
                || listing.PricePerUnit.HasValue
                || (listing.Size != null && listing.Size.Count > 0)
                || !string.IsNullOrWhiteSpace(listing.Configuration)
                || !string.IsNullOrWhiteSpace(listing.ProjectName);
            if (!hasMarketFact) return false;

            return true;
        }

        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        //  NORMALIZE TEXT  (Hindi -> English, symbols)
        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            text = text
                .Replace("\u202F", " ").Replace("\u00A0", " ")
                .Replace("Rs.", "Rs").Replace("\u20B9", " Rs ")
                .Replace("\u2013", "-").Replace("\u2014", "-")
                .Replace("\u00D7", "x")
                .Replace("*", " ").Replace("_", " ")
                // Size units (compound first)
                .Replace("sq. ft.", "sqft", StringComparison.OrdinalIgnoreCase)
                .Replace("sq ft.", "sqft", StringComparison.OrdinalIgnoreCase)
                .Replace("sq.ft.", "sqft", StringComparison.OrdinalIgnoreCase)
                .Replace("sq ft", "sqft", StringComparison.OrdinalIgnoreCase)
                .Replace("sq feet", "sqft", StringComparison.OrdinalIgnoreCase)
                .Replace("square feet", "sqft", StringComparison.OrdinalIgnoreCase)
                .Replace("square foot", "sqft", StringComparison.OrdinalIgnoreCase)
                .Replace("sq.yd", "sqyard", StringComparison.OrdinalIgnoreCase)
                // Hindi size units
                .Replace("\u0938\u094D\u0915\u094D\u0935\u093E\u092F\u0930 \u092B\u0940\u091F", " sqft ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0935\u0930\u094D\u0917 \u092B\u0940\u091F", " sqft ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0935\u0930\u094D\u0917 \u092B\u093C\u0940\u091F", " sqft ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u0930 \u0938\u094D\u0915\u094D\u0935\u093E\u092F\u0930", " per sqft ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u090F\u0915\u0921\u093C", " acre ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u090F\u0915\u0921", " acre ", StringComparison.OrdinalIgnoreCase)
                // TCS Square before individual tokens
                .Replace("\u091F\u0940\u0938\u0940\u090F\u0938 \u0938\u094D\u0915\u094D\u0935\u093E\u092F\u0930", " TCS Square ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u091F\u0940\u0938\u0940\u090F\u0938", " TCS ", StringComparison.OrdinalIgnoreCase)
                // Localities BEFORE number words
                .Replace("\u0938\u0941\u092A\u0930 \u0915\u0949\u0930\u093F\u0921\u094B\u0930", " super corridor ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0907\u0902\u0926\u094C\u0930", " indore ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0914\u0930\u0940\u0935\u093F\u0902\u0926\u094B", " auravindo ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0914\u0930\u0935\u093F\u0902\u0926\u094B", " auravindo ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0909\u091C\u094D\u091C\u0948\u0928 \u0930\u094B\u0921", " ujjain road ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0916\u0902\u0921\u0935\u093E \u0930\u094B\u0921", " khandwa road ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0928\u0947\u092E\u093E\u0935\u0930 \u0930\u094B\u0921", " neemavar road ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0928\u0940\u092E\u093E\u0935\u0930 \u0930\u094B\u0921", " neemavar road ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0930\u093F\u0902\u0917 \u0930\u094B\u0921", " ring road ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u090F\u092C\u0940 \u0930\u094B\u0921", " ab road ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u093E\u092F\u092A\u093E\u0938", " bypass road ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0926\u0947\u0935\u093E\u0938 \u0928\u093E\u0915\u093E", " dewas naka ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0935\u093F\u091C\u092F \u0928\u0917\u0930", " vijay nagar ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092E\u0939\u093E\u0932\u0915\u094D\u0937\u094D\u092E\u0940 \u0928\u0917\u0930", " mahalaxmi nagar ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092E\u0939\u093E\u0932\u0915\u094D\u0937\u094D\u092E\u0940", " mahalaxmi nagar ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092E\u0939\u093E\u0932\u0915\u094D\u0937\u094D\u092E\u093F", " mahalaxmi nagar ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0916\u091C\u0930\u093E\u0928\u093E", " khajrana ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u0928\u093E\u0921\u093C\u093F\u092F\u093E \u0930\u094B\u0921", " kanadia road ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u0928\u093E\u0921\u093C\u093F\u092F\u093E", " kanadia ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u0928\u093E\u0921\u093F\u092F\u093E", " kanadia ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u0932\u093E\u0938\u093F\u092F\u093E", " palasia ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u0902\u0917\u093E\u0932\u0940 \u0938\u094D\u0915\u094D\u0935\u093E\u092F\u0930", " bengali square ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0928\u093F\u092A\u093E\u0928\u093F\u092F\u093E", " nipania ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092E\u093E\u0902\u0917\u0932\u093F\u092F\u093E", " mangaliya ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u093E\u0902\u0935\u0947\u0930", " sanwer ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u0940\u0925\u092E\u092A\u0941\u0930", " pithampur ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092E\u0939\u0942", " mhow ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u093F\u091A\u094B\u0932\u0940", " bicholi hapsi ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0930\u093E\u091C\u094B\u0926\u093E", " rajoda ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u094D\u0915\u0940\u092E", " scheme ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0927\u093E\u0930 \u0930\u094B\u0921", " Dhar Road ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u091C\u0941\u0928\u093E\u0930\u094D\u0921\u093E \u092A\u0941\u0935\u0930\u094D\u0926\u093E", " Junarda Puvarda ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u091C\u0941\u0928\u093E\u0930\u094D\u0926 \u092A\u0941\u0935\u0930\u094D\u0926\u093E", " Junarda Puvarda ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u0921\u093C\u092C\u0902\u0917\u0930\u0921\u093E", " Badbangarda ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u093F\u0902\u0939\u093E\u0938\u093E", " Sinhasa ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u0902\u091A\u0926\u0947\u0930\u093F\u092F\u093E", " Panchderia ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0927\u0930\u094D\u092E\u092A\u0942\u0930\u0940", " Dharampuri ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u093E\u0932\u093E\u0916\u0947\u0921\u093C\u0940", " palakhedi ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u093E\u0932\u093E\u0916\u0947\u0921\u0940", " palakhedi ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u093E\u0932\u093E\u0916\u0921\u093C\u0940", " palakhedi ", StringComparison.OrdinalIgnoreCase)
                // Number words AFTER localities
                .Replace("\u0915\u0930\u094B\u0921\u093C", " crore ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u0930\u094B\u0921", " crore ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0932\u093E\u0916", " lakh ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u093F\u0917\u093E", " bigha ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u0940\u0918\u093E", " bigha ", StringComparison.OrdinalIgnoreCase)
                // Property & listing type keywords
                .Replace("\u0930\u0947\u091F", " rate ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0928\u0947\u091F", " net ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u094D\u0930\u094B\u091C\u0947\u0915\u094D\u091F", " project ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u094D\u0932\u0949\u091F", " plot ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u094D\u0932\u093E\u091F", " plot ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092B\u094D\u0932\u0948\u091F", " flat ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092E\u0915\u093E\u0928", " house ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u091C\u092E\u0940\u0928", " land ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0926\u0941\u0915\u093E\u0928", " shop ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u0902\u0917\u0932\u093E", " bungalow ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0935\u093F\u0932\u093E", " villa ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0917\u094B\u0926\u093E\u092E", " godown ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092D\u0942\u0916\u0902\u0921", " plot ", StringComparison.OrdinalIgnoreCase)
                // Listing intent
                .Replace("\u0938\u0947\u0932 \u0915\u0930\u0928\u093E \u0939\u0948", " for sale ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u0947\u0932 \u0915\u0930\u0928\u093E", " for sale ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u0947\u0932 \u0939\u0948", " for sale ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092B\u0949\u0930 \u0938\u0947\u0932", " for sale ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u0947\u0932", " sale ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u093F\u0930\u093E\u090F \u0938\u0947 \u0926\u0947\u0928\u093E \u0939\u0948", " available for rent ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u093F\u0930\u093E\u090F \u0938\u0947 \u0926\u0947\u0928\u0940 \u0939\u0948", " available for rent ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u093F\u0930\u093E\u092F\u093E", " rent ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u093F\u0930\u093E\u092F\u0947", " rent ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u093F\u0930\u093E\u090F", " rent ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u0947\u091A\u0928\u093E \u0939\u0948", " for sale ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u0947\u091A\u0928\u093E", " sale ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u093F\u0915\u094D\u0930\u0940", " sale ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u093F\u0915\u093E\u090A", " for sale ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0909\u092A\u0932\u092C\u094D\u0927", " available ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0916\u0930\u0940\u0926\u0928\u093E \u0939\u0948", " requirement ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0916\u0930\u0940\u0926\u0928\u093E", " buy ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0916\u0930\u0940\u0926\u0940", " purchase ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u091A\u093E\u0939\u093F\u090F", " chahiye ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0932\u0947\u0928\u093E \u0939\u0948", " required ", StringComparison.OrdinalIgnoreCase)
                // Property detail words
                .Replace("\u0938\u093E\u0907\u091C", " size ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u093E\u0907\u091C\u093C", " size ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0930\u091C\u093F\u0938\u094D\u091F\u094D\u0930\u0940", " registry ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0928\u093E\u092E\u093E\u0902\u0924\u0930\u0923", " namantaran ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u0930\u092E\u093F\u0936\u0928", " permission ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092B\u094D\u0932\u094B\u0930", " floor ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092E\u0902\u091C\u093C\u093F\u0932", " floor ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092E\u0902\u091C\u093F\u0932", " floor ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u0947\u0938\u092E\u0947\u0902\u091F", " basement ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u093E\u0930\u094D\u0915\u093F\u0902\u0917", " parking ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0926\u0942\u0930\u0940", " distance ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u0941\u0915\u093F\u0902\u0917", " booking ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0928\u093F\u0935\u0947\u0936", " investment ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u0948\u0902\u0915 \u0932\u094B\u0928", " bank loan ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0932\u094B\u0928", " loan ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0905\u0930\u094D\u091C\u0947\u0902\u091F", " urgent ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0905\u0930\u094D\u091C\u0947\u0928\u094D\u091F", " urgent ", StringComparison.OrdinalIgnoreCase)
                // Project name translations
                .Replace("\u0915\u0932\u094D\u092A\u0928\u093E \u090F\u0935\u0947\u0928\u094D\u092F\u0942", " Kalpana Avenue ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u093F\u0902\u0917\u093E\u092A\u0941\u0930 \u0932\u093E\u0907\u092B \u0938\u094D\u091F\u093E\u0907\u0932", " Singapore Lifestyle ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u093F\u0902\u0917\u093E\u092A\u0941\u0930", " Singapore ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u090F\u0935\u0947\u0928\u094D\u092F\u0942", " Avenue ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u091F\u094D\u0930\u0947\u091C\u0930 \u0921\u094D\u0930\u0940\u092E\u094D\u0938", " Treasure Dreams ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u091F\u094D\u0930\u0947\u091C\u0930 \u092B\u0947\u0902\u091F\u0947\u0938\u0940", " Treasure Fantasy ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u093E\u0932\u093F\u0902\u0926\u0940 \u0917\u094B\u0932\u094D\u0921", " Kalindi Gold ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u0948\u0932\u093F\u092B\u094B\u0930\u094D\u0928\u093F\u092F\u093E \u0938\u093F\u091F\u0940", " California City ", StringComparison.OrdinalIgnoreCase)
                // Contact / directions
                .Replace("\u0938\u0902\u092A\u0930\u094D\u0915 \u0915\u0930\u0947\u0902", " contact ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u0902\u092A\u0930\u094D\u0915 \u0915\u0930\u0947", " contact ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u0902\u092A\u0930\u094D\u0915", " contact ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092B\u0947\u0938\u093F\u0902\u0917", " facing ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0908\u0938\u094D\u091F", " east ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0935\u0947\u0938\u094D\u091F", " west ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u0947\u0938\u094D\u091F", " west ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0928\u093E\u0930\u094D\u0925", " north ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u093E\u0909\u0925", " south ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0914\u0930", " and ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u091F\u0942", " to ", StringComparison.OrdinalIgnoreCase)
                // Amenities / misc
                .Replace("\u0915\u0949\u0930\u094D\u0928\u0930", " corner ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0917\u093E\u0930\u094D\u0921\u0928", " garden ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092B\u0940\u091F", " ft ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092B\u093F\u091F", " ft ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0930\u094B\u0921", " road ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u0930", " per ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u094D\u0930\u0924\u093F", " per ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0930\u0947\u0932\u0935\u0947", " railway ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0932\u093E\u0907\u0928", " line ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092E\u093F\u0928\u091F", " minutes ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u0942\u0930\u093E", " full ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u093E\u0930\u094D\u0915", " park ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u0947", " from ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u0940", " ", StringComparison.OrdinalIgnoreCase)
                // Additional missing translations (from RawText analysis)
                .Replace("\u0921\u093F\u092E\u093E\u0902\u0921", " demand ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u093E\u0908\u091C\u093C", " size ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u093E\u0907\u091C", " size ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0930\u0947\u0902\u091F", " rent ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u093E\u0902\u091F\u0947\u0915\u094D\u091F", " contact ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0917\u094D\u0930\u093E\u092E", " village ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0921\u093E\u0907\u092E\u0947\u0902\u0936\u0928", " dimension ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u091C\u093C\u092E\u0940\u0928", " land ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0932\u094B\u0915\u0947\u0936\u0928", " location ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u093E\u0938", " near ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0928\u091C\u0926\u0940\u0915", " near ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0928\u091C\u093C\u0926\u0940\u0915", " near ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0939\u093E\u0908\u0935\u0947", " highway ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0939\u093E\u0907\u0935\u0947", " highway ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u0947\u0915\u094D\u091F\u0930", " sector ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u0949\u0932\u094B\u0928\u0940", " colony ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u093E\u0932\u094B\u0928\u0940", " colony ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0928\u0917\u0930", " nagar ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0935\u093F\u0939\u093E\u0930", " vihar ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0930\u0947\u091C\u0940\u0921\u0947\u0902\u0938\u0940", " residency ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u091F\u093E\u0909\u0928\u0936\u093F\u092A", " township ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092B\u0947\u091C", " phase ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u094D\u0932\u0949\u0915", " block ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092A\u094D\u0932\u093E\u0938", " place ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u092E\u0930\u094D\u0936\u093F\u092F\u0932", " commercial ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0930\u0947\u091C\u093F\u0921\u0947\u0902\u0936\u093F\u092F\u0932", " residential ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0930\u0939\u093F\u0935\u093E\u0938\u0940", " residential ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0921\u092C\u0932", " double ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0938\u093F\u0902\u0917\u0932", " single ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0913\u092A\u0928", " open ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0915\u0902\u0938\u094D\u091F\u094D\u0930\u0915\u094D\u0936\u0928", " construction ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u092C\u0941\u0915\u093F\u0902\u0917", " booking ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u091F\u094B\u091F\u0932", " total ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0905\u092A\u094D\u0930\u0942\u0935\u094D\u0921", " approved ", StringComparison.OrdinalIgnoreCase)
                .Replace("\u0930\u091C\u093F\u0938\u094D\u091F\u0930\u094D\u0921", " registered ", StringComparison.OrdinalIgnoreCase);

            // Strip /- price suffix  (4550/- -> 4550)
            text = Regex.Replace(text, @"(\d)/-", "$1");
            // Strip "This message was edited" artifact
            text = Regex.Replace(text, @"\s*<This message was edited>\s*", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"follow\s*us\s*:?.*", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"https?://\S+", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"www\.\S+", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bngr\b", "nagar", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bnr\b", "near", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[\u25AA\u2022\u25C6\u2605\u2606\u25A0\u25CF\u2705\u2714\uFE0F\u260E\u2728\u2611\u231B\u2460-\u2468\u277E\u2783\u24F9-\u24FC\p{Cs}]+", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            return text;
        }

        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        //  INDIVIDUAL FIELD EXTRACTORS
        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

        private static string ExtractListingType(string text)
        {
            string lower = NormalizeText(text).ToLowerInvariant();

            if (Regex.IsMatch(lower, @"\b(required|requirement|wanted|need|buy|purchase|chahiye|looking for|client required|urgent required|urgently required)\b")
                || text.Contains("\u091A\u093E\u0939\u093F\u090F", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u0932\u0947\u0928\u093E \u0939\u0948", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u0916\u0930\u0940\u0926\u0928\u093E", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u0916\u0930\u0940\u0926\u0940", StringComparison.OrdinalIgnoreCase))
                return "Requirement";

            // Rent checked BEFORE Sale
            if (Regex.IsMatch(lower, @"\b(rent|rental|lease|monthly|available for rent)\b")
                || text.Contains("\u0915\u093F\u0930\u093E\u092F\u093E", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u0915\u093F\u0930\u093E\u092F\u0947", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u0915\u093F\u0930\u093E\u090F", StringComparison.OrdinalIgnoreCase))
                return "Rent";

            if (Regex.IsMatch(lower, @"\b\d+\s*(bhk|rk)\b", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(lower, @"\b\d+(?:\.\d+)?\s*k\b", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(lower, @"\b(?:sale|sell|for sale|lakh|lac|cr|crore|per sqft|rate)\b", RegexOptions.IgnoreCase))
                return "Rent";

            if (Regex.IsMatch(lower, @"\b(sale|sell|for sale|available|rate|demand|project|bikau|bechna)\b")
                || text.Contains("\u092C\u0947\u091A\u0928\u093E", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u092C\u093F\u0915\u094D\u0930\u0940", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u092C\u093F\u0915\u093E\u090A", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u0909\u092A\u0932\u092C\u094D\u0927", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u0938\u0947\u0932", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u0921\u093F\u092E\u093E\u0902\u0921", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u092B\u093E\u092F\u0926\u0947 \u0915\u093E \u0938\u094C\u0926\u093E", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\u092B\u093C\u093E\u092F\u0926\u0947 \u0915\u093E \u0938\u094C\u0926\u093E", StringComparison.OrdinalIgnoreCase))
                return "Sale";

            return string.Empty;
        }

        private static string ExtractPropertyType(string text)
        {
            string lower = text.ToLowerInvariant();

            // Agricultural
            if (lower.Contains("farmhouse with land") || lower.Contains("farm house with land")) return "Farmhouse with Land";
            if (lower.Contains("orchard") || lower.Contains("fruit farm")) return "Orchard / Fruit Farm";
            if (lower.Contains("dairy farm") || lower.Contains("poultry farm") || lower.Contains("poultry")) return "Dairy / Poultry Farm";
            if (lower.Contains("irrigated land") || lower.Contains("irrigated")) return "Irrigated Land";
            if (lower.Contains("na converted") || lower.Contains("na plot") || lower.Contains("na converted plot")) return "NA Converted Plot";
            if (lower.Contains("plantation land") || lower.Contains("plantation")) return "Plantation Land";
            if (lower.Contains("agricultural land") || lower.Contains("agricultural plot") || lower.Contains("krishi land") || lower.Contains("krishi") || lower.Contains("farm land") || lower.Contains("farmland")) return "Agricultural Land";

            // Commercial
            if (lower.Contains("office space") || lower.Contains("office") || lower.Contains("workspace") || lower.Contains("it park")) return "Office Space";
            if (lower.Contains("shop") || lower.Contains("showroom") || lower.Contains("retail")) return "Shop / Showroom / Retail";
            if (lower.Contains("warehouse") || lower.Contains("godown")) return "Warehouse / Godown";
            if (lower.Contains("factory") || lower.Contains("industrial")) return "Factory / Industrial";
            if (lower.Contains("hotel") || lower.Contains("guest house") || lower.Contains("guesthouse")) return "Hotel / Guest House";
            if (lower.Contains("hospital") || lower.Contains("clinic") || lower.Contains("dispensary")) return "Hospital / Clinic";
            if (lower.Contains("school") || lower.Contains("college") || lower.Contains("university")) return "School / College";
            if (lower.Contains("petrol pump") || lower.Contains("mall")) return "Petrol Pump / Mall";

            // Residential
            if (lower.Contains("studio") || lower.Contains("1rk")) return "Studio / 1RK";
            if (lower.Contains("pg") || lower.Contains("hostel") || lower.Contains("paying guest")) return "PG / Hostel";
            if (lower.Contains("builder floor") || lower.Contains("independent floor")) return "Builder Floor";
            if (lower.Contains("farmhouse") || lower.Contains("farm house")) return "Farmhouse";
            if (lower.Contains("villa") || lower.Contains("row house") || lower.Contains("rowhouse") || lower.Contains("duplex") || lower.Contains("penthouse")) return "Villa / Row House";
            if (lower.Contains("independent house") || lower.Contains("bungalow") || lower.Contains("kothi")) return "Independent House / Bungalow";
            if (lower.Contains("plot") || lower.Contains("land")) return "Plot / Land";
            if (lower.Contains("flat") || lower.Contains("apartment") || Regex.IsMatch(lower, @"\b\d+\s*(bhk|rk)\b", RegexOptions.IgnoreCase)) return "Flat / Apartment";
            if (lower.Contains("house") || lower.Contains("home") || lower.Contains("makan") || lower.Contains("makaan")) return "Independent House / Bungalow";
            if (lower.Contains("commercial")) return "Office Space"; // default commercial fallback

            return string.Empty;
        }

        private static string ExtractConfiguration(string text)
        {
            var m = Regex.Match(text, @"\b(\d+)\s*BHK\b", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value + "BHK";
            var rk = Regex.Match(text, @"\b(\d+)\s*RK\b", RegexOptions.IgnoreCase);
            return rk.Success ? rk.Groups[1].Value + "RK" : string.Empty;
        }

        /// <summary>
        /// Extracts ALL sizes (e.g. "600,800,1000 sqft" \u2192 [600, 800, 1000]).
        /// Returns null if nothing found.
        /// </summary>
        private static List<decimal>? ExtractAllSizes(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            // "25x62=1550 sqft" \u2014 explicit product
            var eq = Regex.Match(text, @"\b\d{1,4}\s*x\s*\d{1,4}\s*=\s*(\d+(?:\.\d+)?)\s*(sqft|sqyard|gaj|bigha|acre)\b",
                RegexOptions.IgnoreCase);
            if (eq.Success && decimal.TryParse(eq.Groups[1].Value, out var ev) && ev > 0)
                return new List<decimal> { ev };

            // Indian comma format 1,00,000 sqft \u2014 ONLY when unit follows
            var ind = Regex.Match(text, @"\b(\d{1,2},\d{2},\d{3})\s*(sqft|sqyard|gaj|bigha|acre)\b",
                RegexOptions.IgnoreCase);
            if (ind.Success && decimal.TryParse(ind.Groups[1].Value.Replace(",", ""), out var iv) && iv > 0)
                return new List<decimal> { iv };

            // Comma/space-separated list before a unit: "600,800,1000 sqft" or "600 , 800 sqft"
            var listMatch = Regex.Match(text,
                @"\b((?:\d{2,6}(?:\.\d+)?[\s,]+){1,9}\d{2,6}(?:\.\d+)?)\s*(sqft|sqyard|gaj|bigha|acre)\b",
                RegexOptions.IgnoreCase);
            if (listMatch.Success)
            {
                var nums = Regex.Matches(listMatch.Groups[1].Value, @"\d{2,6}(?:\.\d+)?")
                    .Cast<Match>()
                    .Select(n => n.Value)
                    .Where(v => decimal.TryParse(v, out var d) && d > 0 && d <= 999999)
                    .Select(decimal.Parse)
                    .Distinct()
                    .OrderBy(v => v)
                    .ToList();
                if (nums.Count > 0) return nums;
            }

            // Labelled: "plot size: 1350 sqft"
            var label = Regex.Match(text,
                @"(?:plot\s*size|flat\s*size|carpet\s*area|built\s*up|size|area)\s*[:\-=]?\s*(\d{2,6}(?:\.\d+)?)\s*(sqft|sqyard|gaj|bigha|acre)\b",
                RegexOptions.IgnoreCase);
            if (label.Success && decimal.TryParse(label.Groups[1].Value, out var lv) && lv > 0)
                return new List<decimal> { lv };

            // Plain: "1350 sqft" \u2014 2-6 digits max (7+ = phone number)
            var plain = Regex.Match(text, @"(?<!\d)(\d{2,6}(?:\.\d+)?)\s*(sqft|sqyard|gaj|bigha|acre)\b",
                RegexOptions.IgnoreCase);
            if (plain.Success && decimal.TryParse(plain.Groups[1].Value, out var pv) && pv > 0)
                return new List<decimal> { pv };

            return null;
        }

        // Kept for IsHighConfidence score (picks largest)
        private static decimal? ExtractSize(string text)
        {
            var list = ExtractAllSizes(text);
            return list != null && list.Count > 0 ? list.Max() : (decimal?)null;
        }

        private static string ExtractSizeUnit(string text)
        {
            var lo = text.ToLowerInvariant();
            if (lo.Contains("sqft")) return "sqft";
            if (lo.Contains("sqyard")) return "sqyard";
            if (lo.Contains("gaj")) return "gaj";
            if (lo.Contains("acre")) return "acre";
            if (lo.Contains("bigha")) return "bigha";
            return string.Empty;
        }

        private static decimal? ExtractWidth(string text)
        {
            var m = Regex.Match(text, @"\b(\d{1,4})\s*[x\u00D7]\s*(\d{1,4})\b", RegexOptions.IgnoreCase);
            return m.Success && decimal.TryParse(m.Groups[1].Value, out var v) ? v : null;
        }

        private static decimal? ExtractLength(string text)
        {
            var m = Regex.Match(text, @"\b(\d{1,4})\s*[x\u00D7]\s*(\d{1,4})\b", RegexOptions.IgnoreCase);
            return m.Success && decimal.TryParse(m.Groups[2].Value, out var v) ? v : null;
        }

        private static decimal? ExtractPricePerUnit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var lower = NormalizeText(text).ToLowerInvariant(); // /- already stripped

            string[] patterns =
            {
                // "Rate: Rs 4500 per sqft" / "rate 4500/sqft"
                @"\brate\s*[:\-]?\s*rs?\.?\s*(\d[\d,]*(?:\.\d+)?)\s*(?:per|/|net)?\s*sqft\b",
                @"\brate\s*[:\-]?\s*(\d[\d,]*(?:\.\d+)?)\s*(?:per|/|net)?\s*sqft\b",
                // "4500 per sqft" / "4500/sqft"
                @"(?<!\d)(\d[\d,]{2,}(?:\.\d+)?)\s*(?:per|/)\s*sqft\b",
                // "Rs 4500 sqft"
                @"\brs\.?\s*(\d[\d,]*(?:\.\d+)?)\s*(?:per\s*)?sqft\b",
                // "demand/rate - 4500" (bare rate without sqft, only when size in sqft present)
                @"\b(?:demand|rate|price)\s*[:\-]?\s*(\d{3,6}(?:\.\d+)?)(?:\s*/-)?(?:\s*(?:rs|per sqft))?\s*$",
            };

            foreach (var pattern in patterns)
            {
                var m = Regex.Match(lower, pattern, RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var raw = m.Groups[1].Value.Replace(",", "");
                    // Sanity: per-sqft rates in Indore are typically 500\u201360000
                    if (decimal.TryParse(raw, out var val) && val >= 300 && val <= 60_000)
                        return val;
                }
            }

            return null;
        }

        private static decimal? ExtractPrice(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var lower = NormalizeText(text).ToLowerInvariant();

            // If a per-sqft rate is present, only accept an explicit total label
            if (Regex.IsMatch(lower, @"\b(?:per|/)\s*sqft\b") || ExtractPricePerUnit(text).HasValue)
            {
                var tm = Regex.Match(lower,
                    @"\b(?:total price|final amount|deal amount|total amount)\s*[:\-]?\s*([\d,]+(?:\.\d+)?)\s*(cr|crore|lakh|lac|k)?\b");
                if (tm.Success)
                {
                    var raw = tm.Groups[1].Value.Replace(",", "");
                    if (decimal.TryParse(raw, out var t)) return ApplyUnit(t, tm.Groups[2].Value);
                }
                return null;
            }

            // Rent typo "rant 20000"
            var rt = Regex.Match(lower, @"\b(?:rent|rant)\s*[:\-]?\s*(\d{4,6})(?:\s*(?:lakh|lac))?\b");
            if (rt.Success && decimal.TryParse(rt.Groups[1].Value, out var rtv)) return rtv;

            // crore / lakh / k
            var crore = Regex.Match(lower, @"(\d+(?:\.\d+)?)\s*(?:cr|crore)\b");
            if (crore.Success && decimal.TryParse(crore.Groups[1].Value, out var cv)) return cv * 10_000_000m;

            // Broker shorthand: "Budget upto 30L" / "demand - 75 L".
            // Require an explicit price/budget label so dimensions such as
            // "30 L x 20 W" are never interpreted as a monetary value.
            var shortLakh = Regex.Match(lower,
                @"\b(?:budget|price|demand|amount)\b[^\d\r\n]{0,16}(\d+(?:\.\d+)?)\s*l\b");
            if (shortLakh.Success && decimal.TryParse(shortLakh.Groups[1].Value, out var slv) && slv < 1000m)
                return slv * 100_000m;

            var lakh = Regex.Match(lower, @"(\d+(?:\.\d+)?)\s*(?:lakh|lac)\b");
            if (lakh.Success && decimal.TryParse(lakh.Groups[1].Value, out var lv))
            {
                // If the number itself is >= 1000 it's almost certainly a per-sqft rate
                // (e.g. "rate 3300 lakh" is a mistranslation of "\u20B93300/sqft") \u2014 skip it
                if (lv < 1000m) return lv * 100_000m;
            }

            var k = Regex.Match(lower, @"(\d+(?:\.\d+)?)\s*k\b");
            if (k.Success && decimal.TryParse(k.Groups[1].Value, out var kv)) return kv * 1_000m;

            // Explicit rent amount: "rent 20000"
            var rent = Regex.Match(lower, @"\b(?:rent|rental)\s*[:\-]?\s*(\d{4,9})\b");
            if (rent.Success && decimal.TryParse(rent.Groups[1].Value, out var rv)) return rv;

            // Indian comma format 3,50,000 \u2014 price (not size since no sqft unit here)
            var ind = Regex.Match(lower, @"\b(\d{1,2},\d{2},\d{3})\b");
            if (ind.Success && decimal.TryParse(ind.Groups[1].Value.Replace(",", ""), out var iv)) return iv;

            return null;
        }

        private static decimal ApplyUnit(decimal value, string unit)
        {
            var normalizedUnit = (unit ?? string.Empty).ToLowerInvariant();
            if (normalizedUnit == "cr" || normalizedUnit == "crore") return value * 10_000_000m;
            if (normalizedUnit == "lakh" || normalizedUnit == "lac") return value * 100_000m;
            if (normalizedUnit == "k") return value * 1_000m;
            return value;
        }

        private static string ExtractPriceUnit(string text)
        {
            string lower = NormalizeText(text).ToLowerInvariant();
            if (Regex.IsMatch(lower, @"\b(?:per|/|net)\s*sqft\b") || ExtractPricePerUnit(text).HasValue)
                return "PerSqFt";
            if (Regex.IsMatch(lower, @"\b(?:per|/)\s*bigha\b") ||
                Regex.IsMatch(lower, @"\blakh\s*(?:per|/)\s*bigha\b"))
                return "PerBigha";
            if (Regex.IsMatch(lower, @"\b(?:per|/)\s*acre\b"))
                return "PerAcre";
            if (Regex.IsMatch(lower, @"\b(?:rent|rental|per month|monthly|p\.m)\b"))
                return "PerMonth";
            if (Regex.IsMatch(lower, @"\b\d+\s*(bhk|rk)\b", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(lower, @"\b\d+(?:\.\d+)?\s*k\b", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(lower, @"\b(?:sale|sell|for sale|lakh|lac|cr|crore|per sqft|rate)\b", RegexOptions.IgnoreCase))
                return "PerMonth";
            if (ExtractPrice(text).HasValue)
                return "Total";
            return string.Empty;
        }

        private static string ExtractFacing(string text)
        {
            // Normalise typos before matching: wast/vest/weast \u2192 west
            text = Regex.Replace(text, @"\bwast\b", "west", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bvest\b", "west", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bweast\b", "west", RegexOptions.IgnoreCase);

            // Multi-direction: "east and west facing"
            var multi = Regex.Match(text,
                @"\b(east|west|north|south)\s*(?:and|\+|&)\s*(east|west|north|south)\s*facing\b",
                RegexOptions.IgnoreCase);
            if (multi.Success)
            {
                var dirs = new[] { multi.Groups[1].Value, multi.Groups[2].Value }
                    .Select(d => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(d.ToLowerInvariant()))
                    .OrderBy(d => "NorthEastSouthWest".IndexOf(d, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return string.Join(" & ", dirs);
            }

            var single = Regex.Match(text, @"\b(east|west|north|south)\s*facing\b", RegexOptions.IgnoreCase);
            return single.Success
                ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(single.Groups[1].Value.ToLowerInvariant())
                : string.Empty;
        }

        private static string NormalizeFacing(string facing)
        {
            if (string.IsNullOrWhiteSpace(facing)) return string.Empty;

            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "East","West","North","South",
                "East & West","North & South","East & South",
                "North & West","North & East","South & West"
            };

            // Strip " Facing" suffix if LLM added it
            var clean = Regex.Replace(facing, @"\s*facing\b", "", RegexOptions.IgnoreCase).Trim();

            // Canonicalise direction order: N before E before S before W
            if (clean.Contains("&"))
            {
                var parts = clean.Split('&')
                    .Select(s => s.Trim())
                    .OrderBy(d => "NorthEastSouthWest".IndexOf(d, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                clean = string.Join(" & ", parts);
            }

            return valid.Contains(clean) ? clean : string.Empty;
        }

        private static string ExtractRoadInfo(string text)
        {
            var parts = new List<string>();

            var road = Regex.Match(text, @"\b(\d+)\s*(ft|feet)\s*road\b", RegexOptions.IgnoreCase);
            if (road.Success) parts.Add(road.Value);
            if (Regex.IsMatch(text, @"\bcorner\b", RegexOptions.IgnoreCase)) parts.Add("Corner Plot");
            if (Regex.IsMatch(text, @"\bgarden\s*facing\b", RegexOptions.IgnoreCase)) parts.Add("Garden Facing");

            return string.Join(", ", parts);
        }

        private static string ExtractFurnishing(string text) => NormalizeFurnishing(text);

        /* PREVIOUS CODE / BACKUP:
        private static string NormalizeFurnishing(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var lo = text.ToLowerInvariant();
            if (lo.Contains("fully furnished") || lo.Contains("full furnished") ||
                lo.Contains("full furnish"))
                return "Fully Furnished";
            if (lo.Contains("semi furnished") || lo.Contains("semi-furnished") ||
                lo.Contains("semi furnish"))
                return "Semi Furnished";
            if (lo.Contains("unfurnished") || lo.Contains("un-furnished"))
                return "Unfurnished";
            if (lo.Contains("furnished"))
                return "Furnished";
            return string.Empty;
        }
        */

        private static string NormalizeFurnishing(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "Unfurnished";
            var lo = text.ToLowerInvariant();
            if (lo.Contains("fully furnished") || lo.Contains("full furnished") ||
                lo.Contains("full furnish"))
                return "Fully Furnished";
            if (lo.Contains("semi furnished") || lo.Contains("semi-furnished") ||
                lo.Contains("semi furnish"))
                return "Semi Furnished";
            if (lo.Contains("unfurnished") || lo.Contains("un-furnished") || 
                lo.Contains("not mention") || lo.Contains("not specified"))
                return "Unfurnished";
            if (lo.Contains("furnished") || lo.Contains("furnish"))
                return "Fully Furnished";
            return "Unfurnished";
        }

        private static string NormalizeListingType(string value)
        {
            var normalized = value == null ? string.Empty : value.Trim().ToLowerInvariant();
            if (normalized == "sale") return "Sale";
            if (normalized == "rent") return "Rent";
            if (normalized == "requirement" || normalized == "req") return "Requirement";
            return string.Empty;
        }

        private static string ExtractLocation(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            // 1. Explicit "Location: ..." label
            var explicitMatch = Regex.Match(text,
                @"(?im)^\s*(?:location|address)\s*[:\-]\s*([^\r\n]+)",
                RegexOptions.IgnoreCase);
            if (explicitMatch.Success)
            {
                var loc = SanitizeLocation(explicitMatch.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(loc)) return loc;
            }

            // 2. Known locality
            var knownMatch = Regex.Match(text,
                @"\b(vijay nagar|mahalaxmi(?:\s+nagar)?|mahalakshmi(?:\s+nagar)?|super corridor|palasia|" +
                @"new palasia|old palasia|nipania|nipaniya|ujjain road|omaxe|saket|rau|" +
                @"satya sai|lig|telephone nagar|babji nagar|race course road|sheraton|scheme 114|scheme 136|" +
                @"khajrana|scheme 140|scheme 78|scheme 54|scheme no\.?\s*\d+|geeta bhawan|" +
                @"pithampur|ring road|khandwa road|mr-?10|mr-?11|mr-?9|mr-?4|pu-?4|" +
                @"indore ujjain road|bicholi mardana|bicholi hapsi|kanadia road|kanadia|" +
                @"bengali square|tilak nagar|silicon city|sanwer|annapurna|" +
                @"pipliyahana|piplihana|navlakha|bhawarkua|bhanwarkuan|alok nagar|" +
                @"dewas naka|talawali chanda|rajoda|palsikar colony|palakhedi|" +
                @"tcs square|auravindo|bypass road|ab road|dharampuri|panchderia|" +
                @"sinhasa|junarda|badbangarda|dhar road|neemavar road|lasudia|mangaliya|" +
                @"kalindi gold|california city|treasure fantasy|treasure dreams|" +
                @"nanda nagar|mhow)\b",
                RegexOptions.IgnoreCase);

            if (knownMatch.Success)
            {
                // Stop at ANY property-detail keyword \u2014 especially facing/corner/garden
                // NOTE: "road" alone is valid in address (Ujjain Road); only "\d ft" stops.
                var stopPat = new Regex(
                    @"\b(east|west|north|south|wast|vest|weast)(\s*(?:and|&|\+)\s*(east|west|north|south|wast|vest|weast))?\s*facing\b|" +
                    @"\b(?:plot size|size|area|rate|demand|price|contact|bhk|sqft|budget|" +
                    @"furnished|rera|facing|corner|garden|open|boundary|for sale|per|@)\b|\(|\b\d+\s*ft\b",
                    RegexOptions.IgnoreCase);
                var endIdx = text.Length;
                var stop = stopPat.Match(text, knownMatch.Index);
                if (stop.Success) endIdx = stop.Index;
                return SanitizeLocation(text.Substring(knownMatch.Index, endIdx - knownMatch.Index));
            }

            var looseMatch = Regex.Match(text,
                @"(?im)^\s*(?:location|loc|address|near)\s*[:\-]?\s*([A-Za-z0-9][A-Za-z0-9\s.,\-]{2,70})$");
            if (looseMatch.Success)
            {
                var loc = SanitizeLocation(looseMatch.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(loc)) return loc;
            }

            var localityLine = Regex.Match(text,
                @"(?im)^\s*([A-Za-z0-9][A-Za-z0-9\s.\-]{2,60}\b(?:nagar|square|road|colony|park|scheme\s*(?:no\.?\s*)?\d+|city|campus|bypass|palasia|nipania|indore)\b[A-Za-z0-9\s.\-]{0,30})\s*$");
            if (localityLine.Success)
            {
                var loc = SanitizeLocation(localityLine.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(loc)) return loc;
            }

            return string.Empty;
        }

        private static string SanitizeLocation(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            // 1. Stop at facing \u2014 handles single/multi-direction + common typos (wast=west, vest=west)
            raw = Regex.Replace(raw,
                @"\b(east|west|north|south|wast|vest|weast)(\s*(?:and|&|\+)\s*(east|west|north|south|wast|vest|weast))?\s*facing\b.*",
                "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 2. Stop at property-detail keywords
            //    "per" catches "super corridor per 2 plot" (Hindi "\u092A\u0930"\u2192"per" artefact)
            //    "sale"/"for sale" stops sub-listing bodies that embed listing type mid-address
            //    NOTE: "road" alone is NOT a stop word \u2014 "Ujjain Road" is part of address.
            raw = Regex.Replace(raw,
                @"\b(plot size|flat size|built up|carpet area|size|area|rate|demand|price|contact|" +
                @"bhk|sqft|sq\.?ft|budget|furnished|rera|floor|dimension|cabin|sitting|workstation|" +
                @"corner|garden|facing|open|boundary|for sale|per)\b.*",
                "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 3. Stop at "N ft road" pattern (road-width detail)
            raw = Regex.Replace(raw,
                @"\b(?:near\s+)?\d+\s*(?:ft|feet|foot)\s+road\b.*",
                "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            raw = Regex.Replace(raw, @"\b\d+\s*(?:ft|feet|foot)\b.*", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 4. Stop at opening parenthesis \u2014 details like "(corner + garden)" are not location
            raw = Regex.Replace(raw, @"\s*\(.*", "");

            // 5. Strip digits glued to sqft/bhk with no space
            raw = Regex.Replace(raw, @"\d+\s*(?:sqft|bhk|rk)\b.*", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 6. Remove phone numbers
            raw = Regex.Replace(raw, @"[6-9]\d{9}", "");
            raw = Regex.Replace(raw, @"[\r\n]+", " ");

            // 7. Strip trailing direction word(s) + connector \u2014 handles:
            //    "... Indore East"          \u2192 remove "East"
            //    "... Indore East And"      \u2192 remove "East And"
            //    "... Indore East And West" \u2192 remove "East And West"
            //    Also catches typos: "Wast", "Vest"
            raw = Regex.Replace(raw,
                @"\s*\b(east|west|north|south|wast|vest|weast)(\s*(?:and|&|\+)\s*(east|west|north|south|wast|vest|weast))?\s*$",
                "", RegexOptions.IgnoreCase);

            // Strip trailing "per" artefact \u2014 "... Super Corridor Per" \u2192 remove "Per"
            raw = Regex.Replace(raw, @"\s+per\s*$", "", RegexOptions.IgnoreCase);

            // 8. Strip trailing lone number \u2014 "... Auravindo Indore 1" \u2192 remove "1"
            raw = Regex.Replace(raw, @"\s+\d+\s*$", "");

            // 9. Remove "Indore Ujjain/Bhopal/..." highway prefix noise \u2014 keep XYZ road part
            raw = Regex.Replace(raw,
                @"^\s*indore\s*[-\u2013]?\s*(ujjain|bhopal|dhar|khandwa|dewas)\b",
                "$1", RegexOptions.IgnoreCase);

            raw = Regex.Replace(raw, @"\s+", " ").Trim(' ', ',', '.', '-', ':');

            if (Regex.IsMatch(raw, @"^mahalaxmi$", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(raw, @"^mahalakshmi$", RegexOptions.IgnoreCase))
                raw += " Nagar";

            // 10. Trim to 70 chars at a word boundary
            if (raw.Length > 70)
            {
                var cut = raw.LastIndexOf(' ', 70);
                raw = (cut > 20 ? raw[..cut] : raw[..70]).Trim();
            }

            // 11. Append Indore only if not already present
            if (!string.IsNullOrWhiteSpace(raw) &&
                !Regex.IsMatch(raw, @"\bindore\b", RegexOptions.IgnoreCase))
                raw += " Indore";

            return ToTitle(raw);
        }

        private static string ExtractProjectName(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            // Quoted: project "Name" or project "Name" (straight or curly quotes)
            var quoted = Regex.Match(text,
                "\\bproject\\s*[\"\u201C\u2018]([^\"\u201C\u201D\u2018\u2019]+)[\"\u201D\u2019]",
                RegexOptions.IgnoreCase);
            if (quoted.Success) return ToTitle(quoted.Groups[1].Value.Trim());

            // Unquoted: project Name ...
            var unquoted = Regex.Match(text,
                @"\bproject\s+([A-Za-z0-9][A-Za-z0-9\s\-]{2,40}?)(?=\s*[,.\n]|\s+(?:plot|flat|rate|sqft|contact|indore|super corridor|$))",
                RegexOptions.IgnoreCase);
            if (unquoted.Success)
            {
                var name = unquoted.Groups[1].Value.Trim(' ', ',', '.');
                if (!IsLocalityName(name)) return ToTitle(name);
            }

            return string.Empty;
        }

        private static bool IsLocalityName(string name)
        {
            var localities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "super corridor","tcs square","palakhedi","auravindo","bicholi hapsi",
                "bicholi mardana","vijay nagar","rajoda","nipania","nipaniya",
                "palsikar colony","bengali square","dewas naka","mahalaxmi nagar",
                "mahalakshmi nagar","rau","mhow","dharampuri","ujjain road",
                "khandwa road","ring road","ab road","bypass road","palasia",
                "new palasia","old palasia","khajrana","pithampur","silicon city",
                "tilak nagar","sanwer","mangaliya","bhawarkua","navlakha",
                "annapurna","pipliyahana","lasudia","saket","kanadia road","kanadia",
                "geeta bhawan","neemavar road","talawali chanda"
            };
            return localities.Contains(name.Trim());
        }

        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        //  CONTACT EXTRACTION
        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

        private static string ExtractPhone(string text)
        {
            var m = Regex.Match(text, @"(?:\+91[\s\-\u00a0]?)?([6-9]\d{2}[\s\-]?\d{3}[\s\-]?\d{4})\b");
            if (!m.Success) return string.Empty;
            var digits = Regex.Replace(m.Value, @"\D", "");
            if (digits.Length == 12 && digits.StartsWith("91")) digits = digits.Substring(2);
            return digits.Length == 10 ? digits : string.Empty;
        }

        private static List<string> ExtractAllPhones(string text)
        {
            var matches = Regex.Matches(text,
                @"(?:\+91[\s\-\u00a0]?)?([6-9]\d{2}[\s\-]?\d{3}[\s\-]?\d{4})\b");
            var result = new List<string>();
            foreach (Match m in matches)
            {
                var digits = Regex.Replace(m.Value, @"\D", "");
                if (digits.Length == 12 && digits.StartsWith("91")) digits = digits.Substring(2);
                if (digits.Length == 10 && !result.Contains(digits))
                    result.Add(digits);
            }
            return result;
        }

        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        //  RAW TEXT BUILDER
        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

        private static string BuildEnglishRawText(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;

            var text = NormalizeText(body);
            text = Regex.Replace(text, @"\bsq\s*fit\b", "sqft", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bsq\s*feet\b", "sqft", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b(\d+)\s*bhk\b", "$1BHK", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\brera\b", "RERA", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\btcs\b", "TCS", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bmpeb\b", "MPEB", RegexOptions.IgnoreCase);
            // Remove phone numbers from RawText
            text = Regex.Replace(text, @"(?:\+91[\s\-]?)?[6-9]\d{9}", "");

            // Strip residual Hindi stop-words / short particles that NormalizeText leaves
            // (these are grammar words with no single English equivalent worth keeping)
            var hindiStopWords = new[]
            {
                "\u0939\u0948", "\u0939\u0948\u0902", "\u0939\u0947", "\u0915\u0947", "\u0915\u093E", "\u0915\u0940", "\u092E\u0947\u0902", "\u092E\u0947", "\u0938\u0947", "\u092A\u0930",
                "\u0915\u094B", "\u0928\u0947", "\u0939\u094B", "\u092D\u0940", "\u0939\u0940", "\u0924\u094B", "\u092F\u0939", "\u0935\u0939", "\u0907\u0938", "\u0909\u0938",
                "\u090F\u0915", "\u0926\u094B", "\u0924\u0940\u0928", "\u0964", "\u0965"
            };
            foreach (var w in hindiStopWords)
                text = text.Replace(w, " ");

            // Strip any remaining Hindi unicode chars
            text = Regex.Replace(text, @"[\u0900-\u097F]+", " ");

            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        private static string ParseAndFormatDateTime(string dateStr, string timeStr)
        {
            if (string.IsNullOrWhiteSpace(dateStr)) return string.Empty;
            
            timeStr = (timeStr ?? "").Replace("\u202F", " ").Replace("\u00A0", " ").Trim();
            
            var dateFormats = new[] {
                "d/M/yyyy", "d/M/yy",
                "dd/MM/yyyy", "dd/MM/yy",
                "yyyy-MM-dd", "M/d/yyyy", "M/d/yy"
            };

            var timeFormats = new[] {
                "h:mm:ss tt", "h:mm tt", "H:mm:ss", "H:mm",
                "hh:mm:ss tt", "hh:mm tt", "HH:mm:ss", "HH:mm"
            };

            DateTime parsedDate = DateTime.MinValue;
            bool dateParsed = false;
            
            foreach (var df in dateFormats)
            {
                if (DateTime.TryParseExact(dateStr, df, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate))
                {
                    dateParsed = true;
                    break;
                }
            }
            
            if (!dateParsed)
            {
                if (!DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate))
                {
                    return string.Empty;
                }
            }

            TimeSpan parsedTime = TimeSpan.Zero;
            bool timeParsed = false;
            
            if (!string.IsNullOrWhiteSpace(timeStr))
            {
                DateTime tempTime;
                foreach (var tf in timeFormats)
                {
                    if (DateTime.TryParseExact(timeStr, tf, CultureInfo.InvariantCulture, DateTimeStyles.None, out tempTime))
                    {
                        parsedTime = tempTime.TimeOfDay;
                        timeParsed = true;
                        break;
                    }
                }
                
                if (!timeParsed)
                {
                    if (DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out tempTime))
                    {
                        parsedTime = tempTime.TimeOfDay;
                        timeParsed = true;
                    }
                }
            }
            
            var finalDateTime = parsedDate.Date.Add(parsedTime);
            return finalDateTime.ToString("dd/MM/yyyy, hh:mm tt", CultureInfo.InvariantCulture);
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //  DEDUPLICATION
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /* PREVIOUS CODE / BACKUP:
        private static List<PropertyListing> DeduplicateListings(List<PropertyListing> listings)
        {
            // Group by content key (WITHOUT SenderName so forwarder duplicates collapse).
            // Within each group, pick the "richest" entry â€” most filled fields wins.
            var groups = new Dictionary<string, List<PropertyListing>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in listings)
            {
                var sizeKey = (item.Size?.Count > 0)
                    ? string.Join(",", item.Size.Select(s => ((int)Math.Round(s / 10m) * 10).ToString()))
                    : "";

                // Key does NOT include SenderName â€” same listing posted by multiple brokers merges
                string key = string.Join("|",
                    item.MessageDate ?? "",
                    item.ListingType ?? "",
                    item.PropertyType ?? "",
                    item.Configuration ?? "",
                    item.Location ?? "",
                    sizeKey,
                    item.PricePerUnit?.ToString() ?? "",
                    item.Price?.ToString() ?? "",
                    item.ProjectName ?? "",
                    NormalizeDedupText(item.RawText ?? ""));

                if (!groups.ContainsKey(key)) groups[key] = new List<PropertyListing>();
                groups[key].Add(item);
            }

            // From each group pick the entry with the most filled fields
            return groups.Values
                .Select(g => g.OrderByDescending(Richness).First())
                .ToList();

            static int Richness(PropertyListing l) =>
                (string.IsNullOrWhiteSpace(l.ContactNumber) ? 0 : 3) +
                (string.IsNullOrWhiteSpace(l.Location) ? 0 : 2) +
                (l.Price.HasValue || l.PricePerUnit.HasValue ? 1 : 0) +
                (l.Size?.Count > 0 ? 1 : 0) +
                (string.IsNullOrWhiteSpace(l.ProjectName) ? 0 : 1) +
                (string.IsNullOrWhiteSpace(l.Facing) ? 0 : 1);
        }
        */

        private static List<PropertyListing> DeduplicateListings(List<PropertyListing> listings)
        {
            /* PREVIOUS CODE / BACKUP:
            // Group by content key (WITHOUT SenderName, MessageDate, or RawText so duplicate postings merge cleanly).
            // Within each group, pick the "richest" entry â€” most filled fields wins.
            var groups = new Dictionary<string, List<PropertyListing>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in listings)
            {
                var sizeKey = (item.Size?.Count > 0)
                    ? string.Join(",", item.Size.Select(s => ((int)Math.Round(s / 10m) * 10).ToString()))
                    : "";

                string key = string.Join("|",
                    (item.ListingType ?? "").Trim().ToLowerInvariant(),
                    (item.PropertyType ?? "").Trim().ToLowerInvariant(),
                    (item.Configuration ?? "").Trim().ToLowerInvariant(),
                    (item.Location ?? "").Trim().ToLowerInvariant(),
                    sizeKey,
                    item.PricePerUnit?.ToString() ?? "",
                    item.Price?.ToString() ?? "",
                    (item.PriceUnit ?? "").Trim().ToLowerInvariant(),
                    (item.ProjectName ?? "").Trim().ToLowerInvariant());

                if (!groups.ContainsKey(key)) groups[key] = new List<PropertyListing>();
                groups[key].Add(item);
            }

            // From each group pick the entry with the most filled fields
            return groups.Values
                .Select(g => g.OrderByDescending(Richness).First())
                .ToList();

            static int Richness(PropertyListing l) =>
                (string.IsNullOrWhiteSpace(l.ContactNumber) ? 0 : 3) +
                (string.IsNullOrWhiteSpace(l.Location) ? 0 : 2) +
                (l.Price.HasValue || l.PricePerUnit.HasValue ? 1 : 0) +
                (l.Size?.Count > 0 ? 1 : 0) +
                (string.IsNullOrWhiteSpace(l.ProjectName) ? 0 : 1) +
                (string.IsNullOrWhiteSpace(l.Facing) ? 0 : 1);
            */

            // New logic: Group by SenderName/Contact + NormalizedText to collapse identical broker postings
            var groups = new Dictionary<string, List<PropertyListing>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in listings)
            {
                string brokerKey = CleanBrokerIdentifier(item.SenderName, item.ContactNumber);
                string textKey = NormalizeForTextDedup(item.RawText);
                string key = $"{brokerKey}|{textKey}";

                if (!groups.ContainsKey(key)) groups[key] = new List<PropertyListing>();
                groups[key].Add(item);
            }

            return groups.Values
                .Select(g => g.OrderByDescending(Richness).First())
                .ToList();

            static int Richness(PropertyListing l) =>
                (string.IsNullOrWhiteSpace(l.ContactNumber) ? 0 : 3) +
                (string.IsNullOrWhiteSpace(l.Location) ? 0 : 2) +
                (l.Price.HasValue || l.PricePerUnit.HasValue ? 1 : 0) +
                (l.Size?.Count > 0 ? 1 : 0) +
                (string.IsNullOrWhiteSpace(l.ProjectName) ? 0 : 1) +
                (string.IsNullOrWhiteSpace(l.Facing) ? 0 : 1);
        }

        private static string CleanBrokerIdentifier(string senderName, string contactNumber)
        {
            var numbers = ExtractAllPhones(contactNumber);
            if (numbers.Count > 0)
            {
                return string.Join(",", numbers.OrderBy(n => n));
            }
            return (senderName ?? "").Trim().ToLowerInvariant();
        }

        private static string NormalizeForTextDedup(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var sb = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString();
        }

        private static string NormalizeDedupText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();
            return text.Length <= 180 ? text : text.Substring(0, 180);
        }

        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        //  HELPER UTILITIES
        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

        private static string StripInvisibleChars(string text) =>
            Regex.Replace(text, @"[\u200b-\u200f\u202a-\u202f\u2060-\u2064\ufeff\u00ad]", " ");

        private static string CleanSenderName(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("\u202F", " ").Replace("\u00A0", " ")
                       .Replace("\u202A", "").Replace("\u202C", "").Trim();
            text = Regex.Replace(text, @"^[~\-\*\s]+", "");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            // Normalise phone-number senders (+91 XXXXX XXXXX)
            if (Regex.IsMatch(text, @"^\+?91[\s\-]?\d[\d\s\-]{8,11}$"))
            {
                var digits = Regex.Replace(text, @"\D", "");
                if (digits.Length == 12 && digits.StartsWith("91")) digits = digits.Substring(2);
                if (digits.Length == 10) return digits;
            }

            return text;
        }

        private static string ToTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = Regex.Replace(value.Trim(), @"\s+", " ");
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
        }

        private static string FormatBlockForLlm(WhatsAppBlock block)
        {
            var dateTimeStr = ParseAndFormatDateTime(block.MessageDate, block.MessageTime);
            return $"""
SenderName: {block.SenderName}
MessageDate: {block.MessageDate}
MessageDateTime: {dateTimeStr}
MessageBody:
{block.MessageBody}
---
""";
        }

        private static List<string> BuildChunksFromBlocks(List<WhatsAppBlock> blocks, int maxCharsPerChunk)
        {
            var chunks = new List<string>();
            var sb = new StringBuilder();

            foreach (var block in blocks.Select(FormatBlockForLlm))
            {
                if (string.IsNullOrWhiteSpace(block)) continue;

                if (sb.Length + block.Length + 2 > maxCharsPerChunk && sb.Length > 0)
                {
                    chunks.Add(sb.ToString());
                    sb.Clear();
                }

                if (block.Length > maxCharsPerChunk)
                {
                    for (int i = 0; i < block.Length; i += maxCharsPerChunk)
                        chunks.Add(block.Substring(i, Math.Min(maxCharsPerChunk, block.Length - i)));
                }
                else
                {
                    sb.AppendLine(block);
                    sb.AppendLine();
                }
            }

            if (sb.Length > 0) chunks.Add(sb.ToString());
            return chunks;
        }

        private static string GetJsonString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value)) return string.Empty;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty
            };
        }

        private static decimal? GetJsonDecimal(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n)) return n;
            if (value.ValueKind == JsonValueKind.String &&
                decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                return p;
            return null;
        }

        // Reads a JSON number OR array of numbers into List<decimal>
        private static List<decimal>? GetJsonDecimalList(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value)) return null;

            // Single number -> wrap in list
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var single))
                return new List<decimal> { single };

            // Array of numbers
            if (value.ValueKind == JsonValueKind.Array)
            {
                var list = new List<decimal>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var d))
                        list.Add(d);
                    else if (item.ValueKind == JsonValueKind.String &&
                             decimal.TryParse(item.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ps))
                        list.Add(ps);
                }
                return list.Count > 0 ? list : null;
            }

            return null;
        }

        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        //  INNER TYPES
        // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

        public class WhatsAppBlock
        {
            public string SenderName { get; set; } = "";
            public string MessageDate { get; set; } = "";
            public string MessageTime { get; set; } = "";
            public string MessageBody { get; set; } = "";
            public string RawBlock { get; set; } = "";
        }
    }

    // â”€â”€ Response model for /embed endpoint â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public class EmbedResult
    {
        public int ListingsEmbedded { get; set; }
        public int ListingsFailed { get; set; }
        public int RequirementsEmbedded { get; set; }
        public int RequirementsFailed { get; set; }
    }
}
