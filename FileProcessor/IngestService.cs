// ============================================================
// FILE: IngestService.cs
// ============================================================
// Complete, self-contained ingestion service.
//
// INTEGRATION STEPS:
//
// 1. Create this file: IngestService.cs in your project root
//    (same folder as Function.cs and PropertyListing.cs)
//
// 2. Add NuGet packages (if not already present):
//    dotnet add package AWSSDK.SQS
//    dotnet add package Npgsql
//    dotnet add package Pgvector.Npgsql
//
// 3. In Function.cs constructor, add these TWO lines:
//
//    _ingestService = new IngestService(_dbConnectionString, _s3Client);
//
//    And add the field:
//    private readonly IngestService _ingestService;
//
// 4. In Function.cs FunctionHandler method, add this route
//    BEFORE the default S3 process block (before line ~52):
//
//    if (path.EndsWith("/ingest"))
//        return await _ingestService.HandleIngestAsync(request, context);
//
// 5. Set environment variable in Lambda config:
//    SQS_QUEUE_URL = <your SQS queue URL for embedding jobs>
//
// 6. Add API Gateway route:
//    POST /ingest → same Lambda
//
// That's it. No other changes to Function.cs needed.
// ============================================================

using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Pgvector.Npgsql;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace propseekr_file_processor
{
    /// <summary>
    /// Handles the /ingest endpoint: reads extracted PropertyListing JSON,
    /// resolves brokers/localities, normalizes fields, inserts into
    /// listings/requirements/listing_sizes, and pushes to SQS.
    /// </summary>
    public class IngestService
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly AmazonS3Client _s3Client;
        private readonly string _sqsQueueUrl;
        private readonly GeocodingService _geocoder;
        private readonly string _defaultCity;

        // ─────────────────────────────────────────────────────
        //  CONSTRUCTOR
        // ─────────────────────────────────────────────────────

        public IngestService(string dbConnectionString, AmazonS3Client s3Client)
        {
            var builder = new NpgsqlDataSourceBuilder(dbConnectionString);
            builder.UseVector();
            _dataSource = builder.Build();

            _s3Client = s3Client;
            _sqsQueueUrl = Environment.GetEnvironmentVariable("SQS_QUEUE_URL") ?? "";
            _geocoder = new GeocodingService();
            _defaultCity = CityExtractor.NormalizeDefaultCity(
                Environment.GetEnvironmentVariable("DEFAULT_CITY"));
        }

        // ─────────────────────────────────────────────────────
        //  MAIN ENDPOINT
        //  POST /ingest
        //  Body option 1: {"bucket":"...","key":"..._listings.json"}
        //  Body option 2: {"listings": [...]}  (inline)
        // ─────────────────────────────────────────────────────

        public async Task<APIGatewayProxyResponse> HandleIngestAsync(
            APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                var body = request.IsBase64Encoded
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(request.Body ?? ""))
                    : request.Body ?? "{}";

                List<PropertyListing> listings;
                string? s3Bucket = null, s3Key = null;

                using var jdoc = JsonDocument.Parse(body);
                var root = jdoc.RootElement;
                var defaultCity = root.TryGetProperty("default_city", out var cityElement)
                    ? CityExtractor.NormalizeDefaultCity(cityElement.GetString())
                    : _defaultCity;

                // Option 1: S3 reference — read JSON file from S3
                if (root.TryGetProperty("bucket", out var bEl) &&
                    root.TryGetProperty("key", out var kEl))
                {
                    s3Bucket = bEl.GetString() ?? "";
                    s3Key = kEl.GetString() ?? "";

                    /* ERRONEOUS CODE / PREVIOUS CODE:
                    context.Logger.LogInformation($"Ingest: reading s3://{s3Bucket}/{s3Key}");
                    var s3Resp = await _s3Client.GetObjectAsync(
                        new GetObjectRequest { BucketName = s3Bucket, Key = s3Key });
                    string rawJson;
                    using (var reader = new StreamReader(s3Resp.ResponseStream, Encoding.UTF8))
                        rawJson = await reader.ReadToEndAsync();
                    */

                    string rawJson = "";
                    var fileName = Path.GetFileName(s3Key);
                    string? foundLocalPath = PropertyListingNormalizer.ResolveLocalFilePath(fileName, context.Logger);

                    if (foundLocalPath != null)
                    {
                        context.Logger.LogInformation($"Ingest: reading local file instead of S3: {foundLocalPath}");
                        rawJson = await File.ReadAllTextAsync(foundLocalPath, Encoding.UTF8);
                    }
                    else
                    {
                        context.Logger.LogInformation($"Ingest: local file not found, downloading from S3: s3://{s3Bucket}/{s3Key}");
                        var s3Resp = await _s3Client.GetObjectAsync(
                            new GetObjectRequest { BucketName = s3Bucket, Key = s3Key });
                        using (var reader = new StreamReader(s3Resp.ResponseStream, Encoding.UTF8))
                            rawJson = await reader.ReadToEndAsync();
                    }

                    listings = ParseListingsFromJson(rawJson);
                }
                // Option 2: Inline listings array in request body
                else if (root.TryGetProperty("listings", out var arrEl))
                {
                    listings = ParseListingsFromElement(arrEl);
                }
                else
                {
                    return Respond(400, new { error = "Body must have {bucket,key} or {listings:[...]}" });
                }

                context.Logger.LogInformation($"Ingest: {listings.Count} listings to process");

                if (listings.Count == 0)
                    return Respond(200, new IngestResult());

                // Open database connection
                await using var conn = await _dataSource.OpenConnectionAsync();

                // Check if this S3 file was already processed (skip duplicates)
                if (s3Bucket != null && s3Key != null)
                {
                    var processedResult = await GetProcessedFileResult(conn, s3Bucket, s3Key);
                    if (processedResult != null)
                    {
                        context.Logger.LogInformation(
                            "Ingest: file was already stored; resuming downstream embedding and matching");
                        return Respond(200, processedResult);
                    }
                }

                // Process all listings in batch
                var result = await ProcessListingsBatch(conn, listings, defaultCity, context.Logger);

                // A batch where every insert failed is a systemic ingestion error,
                // not a successfully processed file. Do not write the idempotency
                // receipt: the durable worker must be able to retry after the schema
                // or configuration problem is corrected.
                if (result.Failed > 0 &&
                    result.ListingsInserted == 0 &&
                    result.RequirementsInserted == 0)
                {
                    return Respond(500, new
                    {
                        error = "All extracted records failed during ingestion.",
                        detail = result.FirstFailure,
                        failed = result.Failed,
                        skipped = result.Skipped
                    });
                }

                // Track processed file in DB
                if (s3Bucket != null && s3Key != null)
                {
                    await TrackProcessedFile(conn, s3Bucket, s3Key, result);
                }

                // Backfill any master rows with NULL lat/lng
                if (result.LocalitiesCreated > 0 || result.ListingsInserted > 0)
                {
                    try
                    {
                        var geocoded = await _geocoder.BackfillMasterCoordinatesAsync(conn);
                        if (geocoded > 0)
                            context.Logger.LogInformation(
                                $"Geocoded {geocoded} master localities with coordinates");
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogError($"Geocoding backfill failed: {ex.Message}");
                    }
                }

                context.Logger.LogInformation(
                    $"Ingest complete. " +
                    $"Listings: {result.ListingsInserted}, " +
                    $"Requirements: {result.RequirementsInserted}, " +
                    $"Brokers: {result.BrokersCreated}, " +
                    $"Localities: {result.LocalitiesCreated}, " +
                    $"Skipped: {result.Skipped}, " +
                    $"Failed: {result.Failed}");

                return Respond(200, result);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Ingest error: {ex}");
                return Respond(500, new { error = "Ingest failed.", detail = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────
        //  SINGLE FORM SUBMISSION
        //  Called by ListingFormService for individual form entries.
        //  Same pipeline as batch ingest but without S3/file tracking.
        // ─────────────────────────────────────────────────────

        public async Task<IngestResult> ProcessSingleFormListing(
            List<PropertyListing> listings, ILambdaLogger log)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            var result = await ProcessListingsBatch(conn, listings, _defaultCity, log);

            // Geocode any new localities
            if (result.LocalitiesCreated > 0)
            {
                try
                {
                    await _geocoder.BackfillMasterCoordinatesAsync(conn);
                }
                catch (Exception ex)
                {
                    log.LogError($"Geocoding backfill failed: {ex.Message}");
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────
        //  BATCH PROCESSOR
        //  Loops through all PropertyListings and inserts each
        //  into the correct table with full normalization.
        // ─────────────────────────────────────────────────────

        private async Task<IngestResult> ProcessListingsBatch(
            NpgsqlConnection conn, List<PropertyListing> listings, string defaultCity, ILambdaLogger log)
        {
            var result = new IngestResult();

            // In-memory caches to avoid repeated DB lookups within the same batch
            var brokerCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var masterCache = new Dictionary<string, MasterResolution>(StringComparer.OrdinalIgnoreCase);

            foreach (var listing in listings)
            {
                try
                {
                    listing.NormalizeCanonicalFields();

                    if (!HasMinimumInsertFacts(listing))
                    {
                        result.Skipped++;
                        log.LogInformation("Skipped incomplete extracted record before DB insert");
                        continue;
                    }

                    // ── Step 1: Resolve broker ───────────────────
                    var phone = CleanPhoneNumber(listing.ContactNumber);
                    if (string.IsNullOrEmpty(phone))
                    {
                        // Fallback: generate a placeholder from sender name
                        var senderKey = (listing.SenderName ?? "anon").Trim();
                        phone = "UNKNOWN_" + Math.Abs(senderKey.GetHashCode()).ToString("X8");
                    }

                    int brokerId;
                    if (brokerCache.TryGetValue(phone, out var cachedBrokerId))
                    {
                        brokerId = cachedBrokerId;
                    }
                    else
                    {
                        var brokerName = !string.IsNullOrWhiteSpace(listing.ContactName)
                            ? listing.ContactName
                            : listing.SenderName ?? "";
                        var (bid, isNew) = await ResolveOrCreateBrokerAsync(conn, phone, brokerName);
                        brokerId = bid;
                        brokerCache[phone] = brokerId;
                        if (isNew) result.BrokersCreated++;
                    }

                    // ── Step 2: Resolve locality → masterid ──────
                    var locationText = listing.Location ?? "";
                    int? masterId = null;
                    var city = CityExtractor.NormalizeDefaultCity(defaultCity);
                    var locationStatus = "missing";
                    string? locationNote = null;

                    if (!string.IsNullOrWhiteSpace(locationText))
                    {
                        city = CityExtractor.ExtractCity(locationText, defaultCity);
                        var cacheKey = $"{city}|{locationText}";
                        MasterResolution resolution;
                        if (masterCache.TryGetValue(cacheKey, out var cachedResolution))
                        {
                            resolution = cachedResolution;
                        }
                        else
                        {
                            var area = CityExtractor.RemoveCityFromLocation(locationText, city);
                            if (string.IsNullOrWhiteSpace(area)) area = locationText.Trim();

                            resolution = await ResolveOrCreateMasterAsync(conn, area, city, _geocoder);
                            masterCache[cacheKey] = resolution;
                            if (resolution.IsNew && resolution.MasterId > 0) result.LocalitiesCreated++;
                        }

                        masterId = resolution.IsTrusted && resolution.MasterId > 0
                            ? resolution.MasterId
                            : null;
                        locationStatus = resolution.Status;
                        locationNote = resolution.Note;
                    }

                    // ── Step 3: Normalize all fields ─────────────
                    var dbListingType = ListingTypeFromRecordKind(listing.RecordKind, listing.ListingType);
                    var dbRequirementType = RequirementTypeFromRecordKind(listing.RecordKind);
                    if (dbListingType == "REQUIREMENT" && dbRequirementType == null)
                        dbRequirementType = ResolveRequirementType(listing);
                    var dbPropertyType = NormalizePropertyTypeForDb(listing.PropertyType);
                    var (dbPrice, dbPriceUnit) = ResolvePriceForDb(listing);
                    (dbPrice, dbPriceUnit) = RemoveUnsupportedInferredPrice(
                        listing, dbPrice, dbPriceUnit);
                    (dbPrice, dbPriceUnit) = NormalizeRentalPriceUnit(
                        dbListingType, dbRequirementType, dbPrice, dbPriceUnit);
                    var dbSize = ResolveSizeForDb(listing);
                    var dbFurnishing = NormalizeFurnishingForDb(listing.Furnishing);
                    var dbFacing = NormalizeFacingForDb(listing.Facing);
                    var dbConfig = !string.IsNullOrWhiteSpace(listing.Configuration)
                        ? listing.Configuration.Trim().ToUpperInvariant()
                        : null;

                    // ── Step 3.5: Data quality validation ────────
                    (dbPrice, dbPriceUnit) = ValidateAndFixPrice(
                        dbPrice, dbPriceUnit, dbSize, dbPropertyType, dbListingType);

                    var contentHash = ComputeContentHash(listing);

                    // ── Step 4: Route to correct table ───────────
                    if (dbListingType == "REQUIREMENT")
                    {
                        var reqType = dbRequirementType ?? "BUY";

                        var reqId = await InsertRequirementAsync(conn, new RequirementInsert
                        {
                            BrokerId = brokerId,
                            MasterIds = masterId.HasValue ? new[] { masterId.Value } : Array.Empty<int>(),
                            City = city,
                            LocationResolutionStatus = locationStatus,
                            LocationResolutionNote = locationNote,
                            RequirementType = reqType,
                            PropertyType = dbPropertyType,
                            Configurations = !string.IsNullOrEmpty(dbConfig)
                                ? new[] { dbConfig }
                                : Array.Empty<string>(),
                            Budget = dbPrice,
                            BudgetUnit = dbPriceUnit,
                            Size = dbSize,
                            FurnishingPref = dbFurnishing,
                            FacingPref = dbFacing,
                            RawMessageText = listing.RawText,
                            ContentHash = contentHash,
                            GroupName = listing.GroupName,
                            MessageDateTime = listing.MessageDateTime
                        });

                        if (reqId.HasValue)
                        {
                            result.RequirementsInserted++;

                            log.LogInformation($"Requirement {reqId} inserted (broker {brokerId})");
                        }
                        else
                        {
                            result.Skipped++; // content_hash duplicate
                        }
                    }
                    else if (dbListingType == "SELL" || dbListingType == "RENT" || dbListingType == "LEASE")
                    {
                        var listingId = await InsertListingAsync(conn, new ListingInsert
                        {
                            BrokerId = brokerId,
                            MasterId = masterId,
                            City = city,
                            LocationResolutionStatus = locationStatus,
                            LocationResolutionNote = locationNote,
                            ListingType = dbListingType,
                            PropertyType = dbPropertyType,
                            Configuration = dbConfig,
                            Price = dbPrice,
                            PriceUnit = dbPriceUnit,
                            Size = dbSize,
                            Furnishing = dbFurnishing,
                            Facing = dbFacing,
                            ProjectName = listing.ProjectName?.Trim(),
                            RoadInfo = listing.RoadInfo?.Trim(),
                            RawMessageText = listing.RawText,
                            ContentHash = contentHash,
                            GroupName = listing.GroupName,
                            MessageDateTime = listing.MessageDateTime
                        });

                        if (listingId.HasValue)
                        {
                            result.ListingsInserted++;

                            // Insert individual sizes into listing_sizes table
                            await InsertListingSizesAsync(
                                conn, listingId.Value, listing.Size, listing.SizeUnit);


                            log.LogInformation($"Listing {listingId} inserted (broker {brokerId})");
                        }
                        else
                        {
                            result.Skipped++; // content_hash duplicate
                        }
                    }
                    else
                    {
                        result.Skipped++;
                        log.LogInformation($"Skipped: unknown ListingType '{listing.ListingType}'");
                    }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.FirstFailure ??= ex.Message;
                    log.LogError($"Failed to ingest listing: {ex.Message}");
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────
        //  BROKER RESOLUTION
        //  Lookup by phone_number. If not found, create new
        //  broker with 10 free credits. Uses ON CONFLICT to
        //  handle race conditions and duplicates.
        // ─────────────────────────────────────────────────────

        private static async Task<(int brokerId, bool isNew)> ResolveOrCreateBrokerAsync(
            NpgsqlConnection conn, string phone, string name)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO brokers (phone_number, name, credit_balance, status, created_at)
                VALUES (@phone, @name, 10, 'ACTIVE', NOW())
                ON CONFLICT (phone_number) DO UPDATE SET
                    last_active_at = NOW(),
                    name = CASE 
                        WHEN LENGTH(EXCLUDED.name) > LENGTH(COALESCE(brokers.name, ''))
                        THEN EXCLUDED.name 
                        ELSE brokers.name 
                    END
                RETURNING brokerid, (xmax = 0) AS is_new
            ", conn);

            cmd.Parameters.AddWithValue("phone", phone);
            cmd.Parameters.AddWithValue("name", string.IsNullOrWhiteSpace(name)
                ? DBNull.Value : (object)name);

            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            var brokerId = reader.GetInt32(0);
            var isNew = reader.GetBoolean(1);
            await reader.CloseAsync();
            return (brokerId, isNew);
        }

        // ─────────────────────────────────────────────────────
        //  MASTER / LOCALITY RESOLUTION
        //  4-step lookup: exact → fuzzy (pg_trgm) → alias → create new
        //  New localities get NULL lat/lng (needs manual review
        //  or geocoding to enable proximity matching).
        // ─────────────────────────────────────────────────────

        internal static async Task<MasterResolution> ResolveOrCreateMasterAsync(
            NpgsqlConnection conn, string area, string city, GeocodingService geocoder)
        {
            var cleanArea = CleanLocationForMaster(area);
            if (string.IsNullOrWhiteSpace(cleanArea))
                cleanArea = area.Trim();

            // Step 1: Exact match on area + city
            await using (var exactCmd = new NpgsqlCommand(@"
                SELECT masterid, area,
                       CASE WHEN lat IS NOT NULL AND lng IS NOT NULL
                            THEN COALESCE(NULLIF(geocoding_status, ''), 'resolved')
                            ELSE COALESCE(NULLIF(geocoding_status, ''), 'pending') END
                FROM master
                WHERE LOWER(BTRIM(area)) = LOWER(BTRIM(@area))
                  AND LOWER(BTRIM(city)) = LOWER(BTRIM(@city))
                LIMIT 1", conn))
            {
                exactCmd.Parameters.AddWithValue("area", cleanArea);
                exactCmd.Parameters.AddWithValue("city", city);
                int? existingId = null;
                string? existingArea = null;
                string? existingStatus = null;
                await using (var reader = await exactCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        existingId = reader.GetInt32(0);
                        existingArea = reader.IsDBNull(1) ? cleanArea : reader.GetString(1);
                        existingStatus = reader.GetString(2);
                    }
                }
                if (existingId.HasValue)
                    return await EnsureTrustedExistingMasterAsync(
                        conn, existingId.Value, existingArea!, city, existingStatus!, geocoder);
            }

            // Step 2: Fuzzy match using pg_trgm (within same city)
            await using (var fuzzyCmd = new NpgsqlCommand(@"
                SELECT masterid, area, similarity(LOWER(area), LOWER(@area)) AS sim,
                       CASE WHEN lat IS NOT NULL AND lng IS NOT NULL
                            THEN COALESCE(NULLIF(geocoding_status, ''), 'resolved')
                            ELSE COALESCE(NULLIF(geocoding_status, ''), 'pending') END
                FROM master
                WHERE LOWER(BTRIM(city)) = LOWER(BTRIM(@city))
                  AND similarity(LOWER(area), LOWER(@area)) >= 0.75
                ORDER BY sim DESC
                LIMIT 1", conn))
            {
                fuzzyCmd.Parameters.AddWithValue("area", cleanArea);
                fuzzyCmd.Parameters.AddWithValue("city", city);
                int? existingId = null;
                string? existingArea = null;
                string? existingStatus = null;
                double similarity = 0;
                await using (var reader = await fuzzyCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        existingId = reader.GetInt32(0);
                        existingArea = reader.IsDBNull(1) ? cleanArea : reader.GetString(1);
                        similarity = reader.GetDouble(2);
                        existingStatus = reader.GetString(3);
                    }
                }
                if (existingId.HasValue)
                    return await EnsureTrustedExistingMasterAsync(
                        conn,
                        existingId.Value,
                        existingArea!,
                        city,
                        existingStatus!,
                        geocoder,
                        $"Matched canonical locality with similarity {similarity:0.00}.");
            }

            // Step 3: Alias match (within same city)
            await using (var aliasCmd = new NpgsqlCommand(@"
                SELECT masterid, area,
                       CASE WHEN lat IS NOT NULL AND lng IS NOT NULL
                            THEN COALESCE(NULLIF(geocoding_status, ''), 'resolved')
                            ELSE COALESCE(NULLIF(geocoding_status, ''), 'pending') END
                FROM master
                WHERE LOWER(BTRIM(city)) = LOWER(BTRIM(@city))
                  AND aliases IS NOT NULL
                  AND LOWER(@area) = ANY(regexp_split_to_array(LOWER(aliases), '\s*[,;|]\s*'))
                LIMIT 1", conn))
            {
                aliasCmd.Parameters.AddWithValue("area", cleanArea);
                aliasCmd.Parameters.AddWithValue("city", city);
                int? existingId = null;
                string? existingArea = null;
                string? existingStatus = null;
                await using (var reader = await aliasCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        existingId = reader.GetInt32(0);
                        existingArea = reader.IsDBNull(1) ? cleanArea : reader.GetString(1);
                        existingStatus = reader.GetString(2);
                    }
                }
                if (existingId.HasValue)
                    return await EnsureTrustedExistingMasterAsync(
                        conn,
                        existingId.Value,
                        existingArea!,
                        city,
                        existingStatus!,
                        geocoder,
                        "Matched an exact canonical locality alias.");
            }

            // Step 4: No match — validate before creating new locality
            // Strategy: Score the text for "locality-ness" vs "garbage-ness"
            // rather than hard rules that reject valid long names

            var isGarbage = IsGarbageLocationText(cleanArea);

            if (isGarbage)
            {
                // Try to salvage: extract first segment before comma/dash
                var segments = cleanArea.Split(new[] { ',', '.', '–', '!', '|' },
                    StringSplitOptions.RemoveEmptyEntries);

                string? salvaged = null;
                foreach (var seg in segments)
                {
                    var trimmed = seg.Trim();
                    if (trimmed.Length >= 3 && trimmed.Length <= 50 && !IsGarbageLocationText(trimmed))
                    {
                        salvaged = trimmed;
                        break;
                    }
                }

                if (salvaged != null)
                {
                    cleanArea = salvaged;
                }
                else
                {
                    // Completely unrecoverable — skip master creation
                    return MasterResolution.Rejected("Location text could not be isolated from the source message.");
                }
            }

            // Auto-geocode new locality
            var geocoding = await geocoder.GeocodeDetailedAsync(cleanArea, city);

            // Multiple API instances can ingest files concurrently. Serialize only
            // the final recheck/insert section so one canonical city/locality row is
            // created without holding the lock during external geocoding.
            await using (var lockCmd = new NpgsqlCommand(
                "SELECT pg_advisory_lock(hashtext('propseekr-master-location'))", conn))
            {
                await lockCmd.ExecuteNonQueryAsync();
            }

            try
            {
                await using (var recheckCmd = new NpgsqlCommand(@"
                    SELECT masterid,
                           CASE WHEN lat IS NOT NULL AND lng IS NOT NULL
                                THEN COALESCE(NULLIF(geocoding_status, ''), 'resolved')
                                ELSE COALESCE(NULLIF(geocoding_status, ''), 'pending') END
                    FROM master
                    WHERE LOWER(BTRIM(area)) = LOWER(BTRIM(@area))
                      AND LOWER(BTRIM(city)) = LOWER(BTRIM(@city))
                    ORDER BY masterid
                    LIMIT 1", conn))
                {
                    recheckCmd.Parameters.AddWithValue("city", city.Trim());
                    recheckCmd.Parameters.AddWithValue("area", cleanArea.Trim());
                    await using var reader = await recheckCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                        return MasterResolution.Existing(reader.GetInt32(0), reader.GetString(1));
                }

                await using var insertCmd = new NpgsqlCommand(@"
                    INSERT INTO master (
                        city, area, aliases, lat, lng, geocoding_status,
                        geocoding_provider, provider_place_id, formatted_address,
                        location_precision, geocoding_confidence, geocoded_at,
                        geocoding_error, review_required)
                    VALUES (
                        @city, @area, @aliases, @lat, @lng, @status,
                        @provider, @place_id, @formatted_address,
                        @precision, @confidence, NOW(), @error, @review_required)
                    RETURNING masterid", conn);
                insertCmd.Parameters.AddWithValue("city", city.Trim());
                insertCmd.Parameters.AddWithValue("area", cleanArea.Trim());
                insertCmd.Parameters.AddWithValue("aliases", GenerateAliases(cleanArea));
                insertCmd.Parameters.AddWithValue("lat", (object?)geocoding.Latitude ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("lng", (object?)geocoding.Longitude ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("status", geocoding.Status);
                insertCmd.Parameters.AddWithValue("provider", geocoding.Provider);
                insertCmd.Parameters.AddWithValue("place_id", (object?)geocoding.PlaceId ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("formatted_address", (object?)geocoding.FormattedAddress ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("precision", (object?)geocoding.Precision ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("confidence", geocoding.Confidence);
                insertCmd.Parameters.AddWithValue("error", (object?)geocoding.Error ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("review_required", !geocoding.IsResolved);
                var newId = (int)(await insertCmd.ExecuteScalarAsync())!;
                return new MasterResolution(
                    newId,
                    true,
                    geocoding.Status,
                    geocoding.IsResolved
                        ? "Resolved with Google server geocoding."
                        : geocoding.Error);
            }
            finally
            {
                await using var unlockCmd = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtext('propseekr-master-location'))", conn);
                await unlockCmd.ExecuteNonQueryAsync();
            }
        }

        private static async Task<MasterResolution> EnsureTrustedExistingMasterAsync(
            NpgsqlConnection conn,
            int masterId,
            string area,
            string city,
            string status,
            GeocodingService geocoder,
            string? note = null)
        {
            if (status is "resolved" or "verified")
                return MasterResolution.Existing(masterId, status, note);

            var result = await geocoder.GeocodeDetailedAsync(area, city);
            await using var update = new NpgsqlCommand(@"
                UPDATE master
                SET lat = @lat, lng = @lng, geocoding_status = @status,
                    geocoding_provider = @provider, provider_place_id = @place_id,
                    formatted_address = @formatted_address, location_precision = @precision,
                    geocoding_confidence = @confidence, geocoded_at = NOW(),
                    geocoding_error = @error, review_required = @review_required
                WHERE masterid = @master_id", conn);
            update.Parameters.AddWithValue("master_id", masterId);
            update.Parameters.AddWithValue("lat", (object?)result.Latitude ?? DBNull.Value);
            update.Parameters.AddWithValue("lng", (object?)result.Longitude ?? DBNull.Value);
            update.Parameters.AddWithValue("status", result.Status);
            update.Parameters.AddWithValue("provider", result.Provider);
            update.Parameters.AddWithValue("place_id", (object?)result.PlaceId ?? DBNull.Value);
            update.Parameters.AddWithValue("formatted_address", (object?)result.FormattedAddress ?? DBNull.Value);
            update.Parameters.AddWithValue("precision", (object?)result.Precision ?? DBNull.Value);
            update.Parameters.AddWithValue("confidence", result.Confidence);
            update.Parameters.AddWithValue("error", (object?)result.Error ?? DBNull.Value);
            update.Parameters.AddWithValue("review_required", !result.IsResolved);
            await update.ExecuteNonQueryAsync();

            return new MasterResolution(
                masterId,
                false,
                result.Status,
                result.IsResolved ? note ?? "Resolved existing canonical locality with Google." : result.Error);
        }

        internal sealed record MasterResolution(
            int MasterId,
            bool IsNew,
            string Status,
            string? Note)
        {
            public bool IsTrusted => Status is "resolved" or "verified";

            public static MasterResolution Existing(int id, string status, string? note = null) =>
                new(id, false, status, note);

            public static MasterResolution Rejected(string note) =>
                new(0, false, "review_required", note);
        }

        // ─────────────────────────────────────────────────────
        //  LISTING INSERT
        //  Inserts into listings table with content_hash dedup.
        //  Returns listingid if inserted, null if duplicate.
        // ─────────────────────────────────────────────────────

        private static async Task<int?> InsertListingAsync(
            NpgsqlConnection conn, ListingInsert data)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO listings (
                    broker_id, master_id, city, source, raw_message_text,
                    location_resolution_status, location_resolution_note, location_resolved_at,
                    listing_type, property_type, configuration,
                    price, price_unit, size, furnishing, facing,
                    project_name, road_info, content_hash,
                    group_name, message_datetime,
                    status, expires_at, last_refreshed_at,
                    freshness_score, freshness_category, freshness_updated_at,
                    created_at, updated_at
                ) VALUES (
                    @broker_id, @master_id, @city, 'WHATSAPP', @raw_text,
                    @location_status, @location_note,
                    CASE WHEN @location_status IN ('resolved', 'verified') THEN NOW() ELSE NULL END,
                    @listing_type, @property_type, @configuration,
                    @price, @price_unit, @size, @furnishing, @facing,
                    @project_name, @road_info, @content_hash,
                    @group_name, @message_datetime,
                    'ACTIVE', NOW() + INTERVAL '30 days', NOW(),
                    100, 'FRESH', NOW(),
                    NOW(), NOW()
                )
                ON CONFLICT (content_hash) WHERE status = 'ACTIVE'
                DO NOTHING
                RETURNING listingid
            ", conn);

            AddParamOrNull(cmd, "broker_id", data.BrokerId);
            AddParamOrNull(cmd, "master_id", data.MasterId);
            AddParamOrNull(cmd, "city", data.City);
            AddParamOrNull(cmd, "location_status", data.LocationResolutionStatus);
            AddParamOrNull(cmd, "location_note", data.LocationResolutionNote);
            AddParamOrNull(cmd, "raw_text", data.RawMessageText);
            AddParamOrNull(cmd, "listing_type", data.ListingType);
            AddParamOrNull(cmd, "property_type", data.PropertyType);
            AddParamOrNull(cmd, "configuration", data.Configuration);
            AddParamOrNull(cmd, "price", data.Price);
            AddParamOrNull(cmd, "price_unit", data.PriceUnit);
            AddParamOrNull(cmd, "size", data.Size);
            AddParamOrNull(cmd, "furnishing", data.Furnishing);
            AddParamOrNull(cmd, "facing", data.Facing);
            AddParamOrNull(cmd, "project_name", data.ProjectName);
            AddParamOrNull(cmd, "road_info", data.RoadInfo);
            AddParamOrNull(cmd, "content_hash", data.ContentHash);
            AddParamOrNull(cmd, "group_name", data.GroupName);

            if (!string.IsNullOrWhiteSpace(data.MessageDateTime) &&
                DateTime.TryParse(data.MessageDateTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var mdt))
            {
                cmd.Parameters.AddWithValue("message_datetime", mdt);
            }
            else
            {
                cmd.Parameters.AddWithValue("message_datetime", DBNull.Value);
            }

            var result = await cmd.ExecuteScalarAsync();
            return (result != null && result != DBNull.Value) ? (int)result : null;
        }

        // ─────────────────────────────────────────────────────
        //  REQUIREMENT INSERT
        //  Inserts into requirements table with content_hash dedup.
        //  Returns requirementid if inserted, null if duplicate.
        // ─────────────────────────────────────────────────────

        private static async Task<int?> InsertRequirementAsync(
            NpgsqlConnection conn, RequirementInsert data)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO requirements (
                    broker_id, city, source, raw_message_text,
                    location_resolution_status, location_resolution_note, location_resolved_at,
                    requirement_type, property_type, configurations,
                    preferred_locality_ids, budget, budget_unit,
                    size, furnishing_pref, facing_pref, content_hash,
                    group_name, message_datetime,
                    status, expires_at,
                    created_at, updated_at
                ) VALUES (
                    @broker_id, @city, 'WHATSAPP', @raw_text,
                    @location_status, @location_note,
                    CASE WHEN @location_status IN ('resolved', 'verified') THEN NOW() ELSE NULL END,
                    @requirement_type, @property_type, @configurations,
                    @locality_ids, @budget, @budget_unit,
                    @size, @furnishing_pref, @facing_pref, @content_hash,
                    @group_name, @message_datetime,
                    'ACTIVE', NOW() + INTERVAL '30 days',
                    NOW(), NOW()
                )
                ON CONFLICT (content_hash) WHERE status = 'ACTIVE'
                DO NOTHING
                RETURNING requirementid
            ", conn);

            AddParamOrNull(cmd, "broker_id", data.BrokerId);
            AddParamOrNull(cmd, "city", data.City);
            AddParamOrNull(cmd, "location_status", data.LocationResolutionStatus);
            AddParamOrNull(cmd, "location_note", data.LocationResolutionNote);
            AddParamOrNull(cmd, "raw_text", data.RawMessageText);
            AddParamOrNull(cmd, "requirement_type", data.RequirementType);
            AddParamOrNull(cmd, "property_type", data.PropertyType);

            // Arrays need special handling for PostgreSQL
            if (data.Configurations.Length > 0)
                cmd.Parameters.AddWithValue("configurations", data.Configurations);
            else
                cmd.Parameters.AddWithValue("configurations", DBNull.Value);

            if (data.MasterIds.Length > 0)
                cmd.Parameters.AddWithValue("locality_ids", data.MasterIds);
            else
                cmd.Parameters.AddWithValue("locality_ids", DBNull.Value);

            AddParamOrNull(cmd, "budget", data.Budget);
            AddParamOrNull(cmd, "budget_unit", data.BudgetUnit);
            AddParamOrNull(cmd, "size", data.Size);
            AddParamOrNull(cmd, "furnishing_pref", data.FurnishingPref);
            AddParamOrNull(cmd, "facing_pref", data.FacingPref);
            AddParamOrNull(cmd, "content_hash", data.ContentHash);
            AddParamOrNull(cmd, "group_name", data.GroupName);

            if (!string.IsNullOrWhiteSpace(data.MessageDateTime) &&
                DateTime.TryParse(data.MessageDateTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var mdt))
            {
                cmd.Parameters.AddWithValue("message_datetime", mdt);
            }
            else
            {
                cmd.Parameters.AddWithValue("message_datetime", DBNull.Value);
            }

            var result = await cmd.ExecuteScalarAsync();
            return (result != null && result != DBNull.Value) ? (int)result : null;
        }

        // ─────────────────────────────────────────────────────
        //  LISTING SIZES INSERT
        //  When Size = [800, 1000], each element gets a row in
        //  listing_sizes. Also converts units to sqft.
        // ─────────────────────────────────────────────────────

        private static async Task InsertListingSizesAsync(
            NpgsqlConnection conn, int listingId,
            List<decimal>? sizes, string? sizeUnit)
        {
            if (sizes == null || sizes.Count == 0) return;

            foreach (var size in sizes)
            {
                decimal sqft = ConvertToSqft(size, sizeUnit ?? "sqft");
                var normalizedSizeUnit = (sizeUnit ?? "sqft").ToLowerInvariant();
                string label;
                if (normalizedSizeUnit == "bigha")
                    label = $"{size} bigha";
                else if (normalizedSizeUnit == "acre")
                    label = $"{size} acre";
                else if (normalizedSizeUnit == "gaj" || normalizedSizeUnit == "yard" || normalizedSizeUnit == "sqyard")
                    label = $"{size} {sizeUnit}";
                else
                    label = $"{size} sqft";

                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO listing_sizes (listing_id, size_sqft, size_label)
                    VALUES (@lid, @sqft, @label)", conn);
                cmd.Parameters.AddWithValue("lid", listingId);
                cmd.Parameters.AddWithValue("sqft", sqft);
                cmd.Parameters.AddWithValue("label", label);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // ─────────────────────────────────────────────────────
        //  SQS PUSH
        //  Pushes a message to SQS for the embedding Lambda
        //  to pick up. Format: {"Type":"LISTING","Id":42}
        // ─────────────────────────────────────────────────────



        // ─────────────────────────────────────────────────────
        //  FILE TRACKING
        //  Prevents re-processing of the same S3 file.
        // ─────────────────────────────────────────────────────

        private static async Task<IngestResult?> GetProcessedFileResult(
            NpgsqlConnection conn, string bucket, string key)
        {
            await using var cmd = new NpgsqlCommand(@"
                SELECT listings_inserted, requirements_inserted,
                       brokers_created, localities_created,
                       skipped_records, failed_records
                FROM processed_files
                WHERE s3_bucket = @bucket AND s3_key = @key
                LIMIT 1", conn);
            cmd.Parameters.AddWithValue("bucket", bucket);
            cmd.Parameters.AddWithValue("key", key);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            return new IngestResult
            {
                ListingsInserted = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                RequirementsInserted = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                BrokersCreated = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                LocalitiesCreated = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                Skipped = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Failed = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
            };
        }

        private static async Task TrackProcessedFile(
            NpgsqlConnection conn, string bucket, string key, IngestResult result)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO processed_files (
                    s3_bucket, s3_key, listings_inserted, requirements_inserted,
                    brokers_created, localities_created, skipped_records,
                    failed_records, processed_at
                ) VALUES (
                    @bucket, @key, @listings, @reqs, @brokers, @localities,
                    @skipped, @failed, NOW())
                ON CONFLICT (s3_bucket, s3_key) DO NOTHING", conn);
            cmd.Parameters.AddWithValue("bucket", bucket);
            cmd.Parameters.AddWithValue("key", key);
            cmd.Parameters.AddWithValue("listings", result.ListingsInserted);
            cmd.Parameters.AddWithValue("reqs", result.RequirementsInserted);
            cmd.Parameters.AddWithValue("brokers", result.BrokersCreated);
            cmd.Parameters.AddWithValue("localities", result.LocalitiesCreated);
            cmd.Parameters.AddWithValue("skipped", result.Skipped);
            cmd.Parameters.AddWithValue("failed", result.Failed);
            await cmd.ExecuteNonQueryAsync();
        }

        // ═════════════════════════════════════════════════════
        //  NORMALIZATION METHODS
        // ═════════════════════════════════════════════════════

        // ── ListingType normalization ────────────────────────

        private static string NormalizeListingTypeForDb(string? listingType)
        {
            var normalized = listingType == null ? string.Empty : listingType.Trim().ToLowerInvariant();
            if (normalized == "sale") return "SELL";
            if (normalized == "rent" || normalized == "rental") return "RENT";
            if (normalized == "lease") return "LEASE";
            if (normalized == "requirement" || normalized == "req") return "REQUIREMENT";
            return string.Empty;
        }

        private static string ListingTypeFromRecordKind(string? recordKind, string? fallbackListingType)
        {
            var rk = (recordKind ?? "").Trim().ToUpperInvariant();
            if (rk == "LISTING_SELL") return "SELL";
            if (rk == "LISTING_RENT") return "RENT";
            if (rk == "LISTING_LEASE") return "LEASE";
            if (rk == "REQ_BUY" || rk == "REQ_RENT" || rk == "REQ_LEASE") return "REQUIREMENT";
            if (rk == "IGNORE") return string.Empty;
            return NormalizeListingTypeForDb(fallbackListingType);
        }

        private static string? RequirementTypeFromRecordKind(string? recordKind)
        {
            var rk = (recordKind ?? "").Trim().ToUpperInvariant();
            if (rk == "REQ_BUY") return "BUY";
            if (rk == "REQ_RENT") return "RENT";
            if (rk == "REQ_LEASE") return "LEASE";
            return null;
        }

        private static bool LooksLikeRequirementIntent(PropertyListing listing)
        {
            var raw = NormalizePriceEvidenceText(
                $"{listing.RawText} {listing.ListingType} {listing.ContactName} {listing.SenderName}");

            return Regex.IsMatch(raw,
                @"\b(required|requirement|req\.?|wanted|need|needed|looking\s+for|client\s+required|urgent\s+required|urgently\s+required|buyer|tenant|chahiye|require)\b",
                RegexOptions.IgnoreCase)
                || Regex.IsMatch(raw,
                    @"\b(?:flat|house|duplex|plot|office|shop|showroom|land|bungalow|villa|\d+\s*bhk)\s+(?:chahiye|required|requirement|wanted|need)\b",
                    RegexOptions.IgnoreCase)
                || Regex.IsMatch(raw,
                    @"\b(?:budget|location).{0,80}\b(?:required|requirement|chahiye|need)\b",
                    RegexOptions.IgnoreCase);
        }

        // ── PropertyType normalization (24 types) ────────────

        private static readonly Dictionary<string, string> PropertyTypeMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Flat"] = "FLAT",
                ["Apartment"] = "FLAT",
                ["Plot"] = "PLOT",
                ["Land"] = "LAND",
                ["Villa"] = "VILLA",
                ["Duplex"] = "DUPLEX",
                ["House"] = "HOUSE",
                ["Independent House"] = "INDEPENDENT_HOUSE",
                ["Independent Floor"] = "INDEPENDENT_FLOOR",
                ["Bungalow"] = "BUNGALOW",
                ["Row House"] = "ROW_HOUSE",
                ["Farm House"] = "FARM_HOUSE",
                ["Farmhouse"] = "FARM_HOUSE",
                ["Penthouse"] = "PENTHOUSE",
                ["Studio"] = "STUDIO",
                ["Shop"] = "SHOP",
                ["Showroom"] = "SHOWROOM",
                ["Office"] = "OFFICE",
                ["Godown"] = "GODOWN",
                ["Warehouse"] = "WAREHOUSE",
                ["Hotel"] = "HOTEL",
                ["Building"] = "BUILDING",
                ["IT Park"] = "IT_PARK",
                ["Commercial"] = "COMMERCIAL",
                ["Industrial"] = "INDUSTRIAL",
                ["Agricultural Land"] = "AGRICULTURAL_LAND",

                // Category mapped residential
                ["Flat / Apartment"] = "FLAT",
                ["Independent House / Bungalow"] = "INDEPENDENT_HOUSE",
                ["Villa / Row House"] = "VILLA",
                ["Plot / Land"] = "PLOT",
                ["PG / Hostel"] = "PG_HOSTEL",
                ["Studio / 1RK"] = "STUDIO",
                ["Builder Floor"] = "BUILDER_FLOOR",
                
                // Category mapped commercial
                ["Office Space"] = "OFFICE",
                ["Shop / Showroom / Retail"] = "SHOP",
                ["Warehouse / Godown"] = "GODOWN",
                ["Factory / Industrial"] = "FACTORY_INDUSTRIAL",
                ["Hotel / Guest House"] = "HOTEL",
                ["Hospital / Clinic"] = "HOSPITAL_CLINIC",
                ["School / College"] = "SCHOOL_COLLEGE",
                ["Petrol Pump / Mall"] = "PETROL_PUMP_MALL",
                
                // Category mapped agricultural
                ["Orchard / Fruit Farm"] = "ORCHARD_FRUIT_FARM",
                ["Dairy / Poultry Farm"] = "DAIRY_POULTRY_FARM",
                ["Farmhouse with Land"] = "FARMHOUSE_WITH_LAND",
                ["Irrigated Land"] = "IRRIGATED_LAND",
                ["NA Converted Plot"] = "NA_CONVERTED_PLOT",
                ["Plantation Land"] = "PLANTATION_LAND"
            };

        private static string? NormalizePropertyTypeForDb(string? propertyType)
        {
            if (string.IsNullOrWhiteSpace(propertyType)) return null;
            return PropertyTypeMap.TryGetValue(propertyType.Trim(), out var mapped)
                ? mapped
                : propertyType.Trim().ToUpperInvariant().Replace(" ", "_");
        }

        private static bool HasMinimumInsertFacts(PropertyListing listing)
        {
            if ((listing.RecordKind ?? "").Trim().Equals("IGNORE", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(listing.ListingType)) return false;
            if (string.IsNullOrWhiteSpace(listing.PropertyType)) return false;

            var hasMarketFact = listing.Price.HasValue
                || listing.PricePerUnit.HasValue
                || (listing.Size != null && listing.Size.Count > 0)
                || !string.IsNullOrWhiteSpace(listing.Configuration)
                || !string.IsNullOrWhiteSpace(listing.ProjectName);
            if (!hasMarketFact) return false;

            // Reject obvious fragment rows like "Rent - 35k" or "Only family"
            // even if the extractor guessed missing fields.
            var raw = listing.RawText ?? "";
            var meaningfulWords = Regex.Matches(raw, @"[A-Za-z0-9]+")
                .Cast<Match>()
                .Select(m => m.Value)
                .Where(w => w.Length > 1)
                .Count();
            if (meaningfulWords < 4) return false;

            return true;
        }

        private static bool RequiresConfiguration(string? propertyType)
        {
            if (string.IsNullOrWhiteSpace(propertyType))
                return false;

            propertyType = propertyType.ToUpperInvariant();
            return propertyType == "FLAT"
                || propertyType == "DUPLEX"
                || propertyType == "HOUSE"
                || propertyType == "VILLA"
                || propertyType == "BUNGALOW"
                || propertyType == "ROW_HOUSE"
                || propertyType == "INDEPENDENT_HOUSE"
                || propertyType == "INDEPENDENT_FLOOR"
                || propertyType == "PENTHOUSE"
                || propertyType == "STUDIO";
        }

        // ── Price resolution ─────────────────────────────────

        private static (decimal? price, string? priceUnit) ResolvePriceForDb(
            PropertyListing listing)
        {
            // Use Price if available, otherwise PricePerUnit
            decimal? price = listing.Price ?? listing.PricePerUnit;

            var sourceUnit = listing.PriceUnit == null ? string.Empty : listing.PriceUnit.Trim();
            string? unit;
            if (sourceUnit == "PerSqFt")
                unit = "PER_SQFT";
            else if (sourceUnit == "PerMonth")
                unit = "PER_MONTH";
            else if (sourceUnit == "PerBigha")
                unit = "PER_BIGHA";
            else if (sourceUnit == "PerAcre")
                unit = "PER_ACRE";
            else if (sourceUnit == "Total")
                unit = "TOTAL";
            else if (string.IsNullOrWhiteSpace(sourceUnit))
                unit = null;
            else
                unit = sourceUnit.ToUpperInvariant();

            return (price, unit);
        }

        // ── Price evidence guard ─────────────────────────────
        //
        // The LLM/local extractor can occasionally infer a price from context
        // even when the original message did not state one. Persisting that
        // guessed value is worse than storing NULL because it creates false
        // affordability matches. Only keep extracted prices that are visibly
        // supported by the raw message text.
        private static (decimal? price, string? priceUnit) RemoveUnsupportedInferredPrice(
            PropertyListing listing, decimal? price, string? priceUnit)
        {
            if (!price.HasValue)
                return (price, priceUnit);

            var evidenceText = listing.RawText ?? "";
            if (string.IsNullOrWhiteSpace(evidenceText))
                return (null, null);

            return HasPriceEvidence(evidenceText, price.Value, priceUnit)
                ? (price, priceUnit)
                : (null, null);
        }

        private static (decimal? price, string? priceUnit) NormalizeRentalPriceUnit(
            string dbListingType, string? dbRequirementType,
            decimal? price, string? priceUnit)
        {
            if (!price.HasValue)
                return (price, priceUnit);

            var isRental = dbListingType == "RENT"
                           || dbListingType == "LEASE"
                           || dbRequirementType == "RENT"
                           || dbRequirementType == "LEASE";

            if (!isRental)
                return (price, priceUnit);

            if (string.IsNullOrWhiteSpace(priceUnit) || priceUnit == "TOTAL")
                return (price, "PER_MONTH");

            return (price, priceUnit);
        }

        private static bool HasPriceEvidence(string text, decimal price, string? priceUnit)
        {
            var normalized = NormalizePriceEvidenceText(text);

            if (!Regex.IsMatch(normalized,
                    @"\b(price|rate|rent|rental|budget|demand|rs|rupee|rupees|inr|lakh|lac|cr|crore|k|per month|monthly|per sqft|sqft)\b",
                    RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(normalized, @"\d{1,3}(?:,\d{2,3})+|\d{4,}"))
            {
                return false;
            }

            var exact = DecimalToPlainString(price);
            var compact = exact.Replace(",", "");

            if (HasBarePriceNumberEvidence(normalized, compact, price, priceUnit))
                return true;

            if (price % 1000m == 0)
            {
                var thousands = DecimalToPlainString(price / 1000m);
                if (Regex.IsMatch(normalized, $@"(?<!\d){Regex.Escape(thousands)}\s*k\b"))
                    return true;
                if (Regex.IsMatch(normalized, $@"(?<!\d){Regex.Escape(thousands)}\s*(?:thousand|thousands)\b"))
                    return true;
            }

            if (price % 100000m == 0)
            {
                var lakh = DecimalToPlainString(price / 100000m);
                if (Regex.IsMatch(normalized, $@"(?<!\d){Regex.Escape(lakh)}\s*(?:lakh|lac)\b"))
                    return true;
            }

            if (price % 10000000m == 0)
            {
                var crore = DecimalToPlainString(price / 10000000m);
                if (Regex.IsMatch(normalized, $@"(?<!\d){Regex.Escape(crore)}\s*(?:cr|crore)\b"))
                    return true;
            }

            // Decimal shorthand: 13.5 lakh, 1.25 cr, etc.
            foreach (Match m in Regex.Matches(normalized,
                         @"(?<!\d)(\d+(?:\.\d+)?)\s*(lakh|lac|cr|crore|k)\b",
                         RegexOptions.IgnoreCase))
            {
                if (!decimal.TryParse(m.Groups[1].Value, NumberStyles.Number,
                        CultureInfo.InvariantCulture, out var value))
                    continue;

                var unit = m.Groups[2].Value.ToLowerInvariant();
                var expanded = value;
                if (unit == "k")
                    expanded = value * 1000m;
                else if (unit == "lakh" || unit == "lac")
                    expanded = value * 100000m;
                else if (unit == "cr" || unit == "crore")
                    expanded = value * 10000000m;

                if (Math.Abs(expanded - price) <= 1m)
                    return true;
            }

            return false;
        }

        private static bool HasBarePriceNumberEvidence(
            string normalized, string compactPrice, decimal price, string? priceUnit)
        {
            foreach (Match match in Regex.Matches(normalized,
                         $@"(?<!\d){Regex.Escape(compactPrice)}(?!\d)"))
            {
                var start = Math.Max(0, match.Index - 28);
                var end = Math.Min(normalized.Length, match.Index + match.Length + 28);
                var window = normalized[start..end];

                var hasMoneyContext = Regex.IsMatch(window,
                    @"\b(price|rate|rent|rental|budget|demand|rs|rupee|rupees|inr|per month|monthly)\b",
                    RegexOptions.IgnoreCase);

                if (hasMoneyContext)
                    return true;

                if (priceUnit == "PER_SQFT" &&
                    Regex.IsMatch(window, @"\b(per sqft|sqft)\b", RegexOptions.IgnoreCase))
                    return true;

                var looksLikeSize = Regex.IsMatch(window,
                    @"\b(sqft|sqyard|gaj|bigha|acre|plot size|built up|carpet|area)\b",
                    RegexOptions.IgnoreCase);

                // Some broker messages use only a bare Indian price number.
                // Keep large bare values, but never when the local context is size/area.
                if (!looksLikeSize && price >= 100000m && priceUnit == "TOTAL")
                    return true;
            }

            return false;
        }

        private static string NormalizePriceEvidenceText(string text)
        {
            return text.ToLowerInvariant()
                .Replace("₹", " rs ")
                .Replace("/-", " ")
                .Replace(",", "")
                .Replace("per sq ft", "per sqft")
                .Replace("sq ft", "sqft");
        }

        private static string DecimalToPlainString(decimal value)
        {
            return decimal.Round(value, 2)
                .ToString("0.##", CultureInfo.InvariantCulture);
        }

        // ── Price validation and sanity checks ───────────────
        //
        // Catches common extraction errors:
        // - TOTAL price < ₹1 lakh for LAND/PLOT (impossible in India)
        // - TOTAL price < ₹50,000 for any property (likely per-sqft rate misclassified)
        // - PER_SQFT rate > ₹1,00,000 (likely total price misclassified)
        // - TOTAL price > ₹500 Cr (likely garbage extraction)
        //
        private static (decimal? price, string? priceUnit) ValidateAndFixPrice(
            decimal? price, string? priceUnit, decimal? size,
            string? propertyType, string? listingType)
        {
            if (price == null) return (price, priceUnit);

            var p = price.Value;

            // Rule 1: TOTAL price suspiciously low — probably PER_SQFT
            if (priceUnit == "TOTAL")
            {
                // Land/Plot with total price < ₹1 lakh — almost certainly per-sqft rate
                if (p < 100_000m && (propertyType == "LAND" || propertyType == "PLOT" || propertyType == "AGRICULTURAL_LAND"))
                {
                    // If size exists, check if treating as per-sqft makes sense
                    if (size.HasValue && size.Value > 0 && p >= 500 && p <= 60_000)
                    {
                        return (p, "PER_SQFT");
                    }
                    // Otherwise null out — data is unreliable
                    return (null, null);
                }

                // Any property with total price < ₹10,000 — garbage
                if (p < 10_000m && listingType == "SELL")
                {
                    return (null, null);
                }

                // Total price > ₹500 Cr — likely extraction error
                if (p > 5_000_000_000m)
                {
                    return (null, null);
                }
            }

            // Rule 2: PER_SQFT rate sanity check
            if (priceUnit == "PER_SQFT")
            {
                // Per-sqft rate < ₹100 — suspiciously low for urban India
                if (p < 100m)
                {
                    return (null, null);
                }

                // Per-sqft rate > ₹1,00,000 — likely total price misclassified
                if (p > 100_000m)
                {
                    // Try treating as TOTAL
                    return (p, "TOTAL");
                }
            }

            // Rule 3: PER_MONTH rent sanity
            if (priceUnit == "PER_MONTH")
            {
                // Monthly rent < ₹1,000 — too low, garbage
                if (p < 1_000m) return (null, null);

                // Monthly rent > ₹50 lakh — likely total price, not monthly
                if (p > 5_000_000m) return (p, "TOTAL");
            }

            // Rule 4: PER_BIGHA / PER_ACRE sanity
            if (priceUnit == "PER_BIGHA" || priceUnit == "PER_ACRE")
            {
                // Less than ₹10,000 per bigha/acre — too low for India
                if (p < 10_000m) return (null, null);
            }

            return (price, priceUnit);
        }

        // ── Size resolution + unit conversion ────────────────

        private static decimal? ResolveSizeForDb(PropertyListing listing)
        {
            if (listing.Size == null || listing.Size.Count == 0) return null;
            var maxSize = listing.Size.Max();
            return ConvertToSqft(maxSize, listing.SizeUnit ?? "sqft");
        }

        private static decimal ConvertToSqft(decimal size, string unit)
        {
            var normalizedUnit = (unit ?? string.Empty).ToLowerInvariant();
            if (normalizedUnit == "bigha") return size * 12000m;
            if (normalizedUnit == "acre") return size * 43560m;
            if (normalizedUnit == "gaj" || normalizedUnit == "yard" || normalizedUnit == "sqyard") return size * 9m;
            return size; // already sqft or unknown
        }

        // ── Furnishing normalization ─────────────────────────

        private static string? NormalizeFurnishingForDb(string? furnishing)
        {
            if (string.IsNullOrWhiteSpace(furnishing)) return "BARE";
            var normalized = furnishing.Trim().ToLowerInvariant();
            if (normalized == "fully furnished" || normalized == "full furnished") return "FURNISHED";
            if (normalized == "semi furnished" || normalized == "semi-furnished") return "SEMI";
            if (normalized == "furnished") return "FURNISHED";
            if (normalized == "unfurnished") return "BARE";
            return "BARE";
        }

        // ── Facing normalization ─────────────────────────────

        private static string? NormalizeFacingForDb(string? facing)
        {
            if (string.IsNullOrWhiteSpace(facing)) return null;

            var validFacings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "East", "West", "North", "South",
                "East & West", "North & South", "East & South",
                "North & West", "North & East", "South & West"
            };

            var clean = facing.Trim();
            return validFacings.Contains(clean) ? clean : null;
        }

        // ── Requirement type resolution ──────────────────────

        private static string ResolveRequirementType(PropertyListing listing)
        {
            var raw = (listing.RawText ?? "").ToLower();
            var normalizedRaw = NormalizePriceEvidenceText(raw);

            if (Regex.IsMatch(normalizedRaw,
                    @"\b(rent|rental|lease|kiraay|kirae|kiraya|kiraye|tenant|on rent|for rent)\b",
                    RegexOptions.IgnoreCase))
                return "RENT";

            if (Regex.IsMatch(normalizedRaw,
                    @"\b(buy|purchase|buyer|sale|sell|resale|registry)\b",
                    RegexOptions.IgnoreCase))
                return "BUY";

            var price = listing.Price ?? listing.PricePerUnit;
            var hasRentalLifestyleSignals = Regex.IsMatch(normalizedRaw,
                @"\b(unfurnished|semi furnished|furnished|family|bachelor|boys|girls|job class|shift|shifting|portion|tenant|play school|school|seater|cabin|workstation)\b",
                RegexOptions.IgnoreCase);
            var hasRentalBudgetText = Regex.IsMatch(normalizedRaw,
                @"\b(budget|udget|bdgt|rent)\b\s*[-:]?\s*\d+\s*k\b|\b\d+\s*k\b",
                RegexOptions.IgnoreCase);

            // Broker rental requirements often omit the word "rent" and say
            // "2BHK semi in Nipania 6k" or "budget 40k". Those are monthly
            // rental budgets, not purchase budgets.
            if ((hasRentalLifestyleSignals || hasRentalBudgetText) &&
                price.HasValue && price.Value >= 1_000m && price.Value <= 200_000m)
            {
                return "RENT";
            }

            return "BUY"; // default for requirement listings
        }

        // ── Phone number cleaning ────────────────────────────

        private static string CleanPhoneNumber(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return "";

            // Take first number if comma/slash separated
            var first = phone.Split(',', '/', '|')[0].Trim();

            // Strip all non-digit characters
            var digits = Regex.Replace(first, @"\D", "");

            // Remove Indian country code
            if (digits.Length == 12 && digits.StartsWith("91"))
                digits = digits[2..];
            if (digits.Length == 11 && digits.StartsWith("0"))
                digits = digits[1..];

            return digits.Length == 10 ? digits : "";
        }

        // ── Location cleaning for master table lookup ────────

        private static string CleanLocationForMaster(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return "";

            // Remove "Indore" suffix
            var clean = Regex.Replace(location, @"\s*\bIndore\b\s*$", "",
                RegexOptions.IgnoreCase).Trim();

            // Remove trailing punctuation
            clean = clean.Trim(' ', ',', '.', '-', ':');

            return string.IsNullOrWhiteSpace(clean) ? location.Trim() : clean;
        }

        // ── Garbage location detection ───────────────────────
        //
        // Uses a scoring system instead of hard rules.
        // A text is "garbage" if it scores 3+ garbage points.
        // This allows long but legitimate names like
        // "Bicholi Hapsi Mayank Blue Water Park Road" to pass.
        //
        private static bool IsGarbageLocationText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;

            var trimmed = text.Trim();
            if (trimmed.Length < 3) return true;
            if (Regex.IsMatch(trimmed, @"^\d+$")) return true; // Reject plain digits like "2"
            if (Regex.IsMatch(trimmed, @"^(from|to|at|in|near|location|address)$", RegexOptions.IgnoreCase))
                return true;

            int score = 0;

            // Emojis or special characters — strong garbage signal
            if (Regex.IsMatch(text, @"[\u2700-\u27BF\uFE00-\uFE0F\p{Cs}🤝👉👈🏠🌟🚀💰📍📰✅⭐🏞️]"))
                score += 3;

            // Brackets, braces — not a locality name
            if (Regex.IsMatch(text, @"[\(\)\[\]\{\}]"))
                score += 2;

            // Marketing/business/transaction/intent words — strong garbage signal
            if (Regex.IsMatch(text, @"\b(offer|opportunity|investment|exclusive|developer|builder|premium|presenting|realities|discover|unlock|compelling|proposition|transform|venture|partnership|ratio|deposit|negotiable|return|yield|profit|revenue|generating|established|performance|rent|sale|lease|buy|purchase|chahiye|chaiye|required|looking|bhk|rk|bachelors|students|family|office|shop|plot|flat|house|villa|duplex|maintenance|call|contact|phone|whatsapp|broker|commission|demand|price|budget|rate)\b",
                RegexOptions.IgnoreCase))
                score += 3;

            // Percentage, rupee signs, currency terms in location
            if (Regex.IsMatch(text, @"[%₹$]") || Regex.IsMatch(text, @"\b(crore|lakh|lac|percent|rs|inr|rupees|rupay)\b", RegexOptions.IgnoreCase))
                score += 3;

            // Very long — more than 80 chars
            if (text.Length > 80) score += 2;

            // Sentence-like structure — contains verbs/articles
            if (Regex.IsMatch(text, @"\b(is|are|was|were|the|this|that|with|for|from|into|has|have|its|your|our|their|looking|seeking|presenting|brings|step|unlock)\b",
                RegexOptions.IgnoreCase))
                score += 2;

            // Contains numbers that look like prices, not addresses
            if (Regex.IsMatch(text, @"\b\d{5,}\b") && !Regex.IsMatch(text, @"\b(scheme|sector|plot|block|phase)\s*\d+\b", RegexOptions.IgnoreCase))
                score += 1;

            // Mild signals (add up but don't reject alone)
            // More than 8 words
            if (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8)
                score += 1;

            // Contains "near" + landmark description (acceptable but borderline)
            // Don't penalize — "Near Prestige University" is valid

            return score >= 3;
        }

        // ── Auto-generate aliases for master table ───────────

        private static string GenerateAliases(string area)
        {
            if (string.IsNullOrWhiteSpace(area)) return "";

            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Original lowercase
            aliases.Add(area.ToLowerInvariant());

            // Without spaces: "Super Corridor" → "supercorridor"
            aliases.Add(Regex.Replace(area, @"\s+", "").ToLowerInvariant());

            // Without hyphens: "MR-10" → "mr10"
            aliases.Add(Regex.Replace(area, @"[-\s]+", "").ToLowerInvariant());

            // With hyphens instead of spaces: "Super Corridor" → "super-corridor"
            aliases.Add(Regex.Replace(area, @"\s+", "-").ToLowerInvariant());

            // Common misspelling patterns
            var lower = area.ToLowerInvariant();

            // Double letters reduced: "nipaniya" → "nipania"
            aliases.Add(Regex.Replace(lower, @"(.)\1+", "$1"));

            // "road" suffix variants
            if (lower.EndsWith(" road"))
            {
                aliases.Add(lower.Replace(" road", ""));
                aliases.Add(lower.Replace(" road", " rd"));
            }

            // "nagar" suffix variants
            if (lower.EndsWith(" nagar"))
            {
                aliases.Add(lower.Replace(" nagar", " ngr"));
            }

            // "square" suffix variants
            if (lower.EndsWith(" square"))
            {
                aliases.Add(lower.Replace(" square", " sq"));
                aliases.Add(lower.Replace(" square", " sqr"));
            }

            // Scheme number variants: "Scheme 140" → "sc 140", "sc.no.140"
            if (lower.StartsWith("scheme "))
            {
                var num = lower.Replace("scheme ", "");
                aliases.Add($"sc {num}");
                aliases.Add($"sc.no.{num}");
                aliases.Add($"scheme no {num}");
                aliases.Add($"scheme no.{num}");
            }

            // Remove original if same as area
            aliases.Remove(area);

            return string.Join(", ", aliases.Where(a => !string.IsNullOrWhiteSpace(a)));
        }

        // ── Content hash for deduplication ───────────────────

        private static string ComputeContentHash(PropertyListing listing)
        {
            // Exclude MessageDate and SenderName to prevent duplicate listings/requirements from entering the database
            // when forwarded by different brokers or at different times.
            var raw = string.Join("|",
                listing.RecordKind ?? "",
                listing.ListingType ?? "",
                listing.PropertyType ?? "",
                listing.Location ?? "",
                listing.Size?.FirstOrDefault().ToString() ?? "",
                listing.Price?.ToString() ?? listing.PricePerUnit?.ToString() ?? "",
                listing.ContactNumber ?? "",
                listing.Configuration ?? "",
                NormalizeHashText(listing.RawText ?? "")
            );
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw.ToLowerInvariant()));
            return Convert.ToHexString(hash)[..16];
        }

        private static string NormalizeHashText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            // Normalize text to be alphanumeric only to collapse minor formatting/emoji differences
            var sb = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }
            var clean = sb.ToString();
            return clean.Length <= 240 ? clean : clean.Substring(0, 240);
        }

        // ═════════════════════════════════════════════════════
        //  JSON PARSING
        // ═════════════════════════════════════════════════════

        private static List<PropertyListing> ParseListingsFromJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("listings", out var arr))
                return ParseListingsFromElement(arr);
            return new List<PropertyListing>();
        }

        private static List<PropertyListing> ParseListingsFromElement(JsonElement arrEl)
        {
            var list = new List<PropertyListing>();
            if (arrEl.ValueKind != JsonValueKind.Array) return list;

            foreach (var item in arrEl.EnumerateArray())
            {
                var listing = new PropertyListing
                {
                    RecordKind = GetStrAny(item, "RecordKind", "recordKind"),
                    SenderName = GetStrAny(item, "SenderName", "senderName"),
                    MessageDate = GetStrAny(item, "MessageDate", "messageDate"),
                    MessageDateTime = GetStrAny(item, "MessageDateTime", "messageDateTime"),
                    GroupName = GetStrAny(item, "GroupName", "groupName"),
                    ListingType = GetStrAny(item, "ListingType", "listingType"),
                    PropertyType = GetStrAny(item, "PropertyType", "propertyType"),
                    Configuration = GetStrAny(item, "Configuration", "configuration"),
                    Location = GetStrAny(item, "Location", "location"),
                    ProjectName = GetStrAny(item, "ProjectName", "projectName"),
                    Size = GetDecimalListAny(item, "Size", "size"),
                    SizeUnit = GetStrAny(item, "SizeUnit", "sizeUnit"),
                    Width = GetDecAny(item, "Width", "width"),
                    Length = GetDecAny(item, "Length", "length"),
                    Price = GetDecAny(item, "Price", "price"),
                    PriceUnit = GetStrAny(item, "PriceUnit", "priceUnit"),
                    PricePerUnit = GetDecAny(item, "PricePerUnit", "pricePerUnit"),
                    Facing = GetStrAny(item, "Facing", "facing"),
                    RoadInfo = GetStrAny(item, "RoadInfo", "roadInfo"),
                    Furnishing = GetStrAny(item, "Furnishing", "furnishing"),
                    ContactName = GetStrAny(item, "ContactName", "contactName"),
                    ContactNumber = GetStrAny(item, "ContactNumber", "contactNumber"),
                    RawText = GetStrAny(item, "RawText", "rawText")
                }.NormalizeCanonicalFields();

                if ((listing.RecordKind ?? "").Trim() != "IGNORE")
                    list.Add(listing);
            }

            return list;
        }

        // ═════════════════════════════════════════════════════
        //  JSON + SQL HELPER METHODS
        // ═════════════════════════════════════════════════════

        private static string GetStr(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v)) return "";
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.ToString(),
                _ => ""
            };
        }

        private static string GetStrAny(JsonElement el, params string[] props)
        {
            foreach (var prop in props)
            {
                var value = GetStr(el, prop);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        private static decimal? GetDec(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String &&
                decimal.TryParse(v.GetString(), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var p)) return p;
            return null;
        }

        private static decimal? GetDecAny(JsonElement el, params string[] props)
        {
            foreach (var prop in props)
            {
                var value = GetDec(el, prop);
                if (value.HasValue) return value;
            }
            return null;
        }

        private static List<decimal>? GetDecimalList(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var single))
                return new List<decimal> { single };
            if (v.ValueKind == JsonValueKind.Array)
            {
                var list = new List<decimal>();
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var d))
                        list.Add(d);
                    else if (item.ValueKind == JsonValueKind.String &&
                             decimal.TryParse(item.GetString(), NumberStyles.Any,
                                 CultureInfo.InvariantCulture, out var ps))
                        list.Add(ps);
                }
                return list.Count > 0 ? list : null;
            }
            return null;
        }

        private static List<decimal>? GetDecimalListAny(JsonElement el, params string[] props)
        {
            foreach (var prop in props)
            {
                var value = GetDecimalList(el, prop);
                if (value != null && value.Count > 0) return value;
            }
            return null;
        }

        private static void AddParamOrNull(NpgsqlCommand cmd, string name, object? value)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        private static APIGatewayProxyResponse Respond(int status, object body) =>
            new APIGatewayProxyResponse
            {
                StatusCode = status,
                Body = JsonSerializer.Serialize(body),
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "Access-Control-Allow-Origin", "*" }
                }
            };

        // ═════════════════════════════════════════════════════
        //  DATA TRANSFER OBJECTS
        // ═════════════════════════════════════════════════════

        private class ListingInsert
        {
            public int BrokerId { get; set; }
            public int? MasterId { get; set; }
            public string City { get; set; } = "Indore";
            public string LocationResolutionStatus { get; set; } = "missing";
            public string? LocationResolutionNote { get; set; }
            public string ListingType { get; set; } = "";
            public string? PropertyType { get; set; }
            public string? Configuration { get; set; }
            public decimal? Price { get; set; }
            public string? PriceUnit { get; set; }
            public decimal? Size { get; set; }
            public string? Furnishing { get; set; }
            public string? Facing { get; set; }
            public string? ProjectName { get; set; }
            public string? RoadInfo { get; set; }
            public string? RawMessageText { get; set; }
            public string? ContentHash { get; set; }
            public string? GroupName { get; set; }
            public string? MessageDateTime { get; set; }
        }

        private class RequirementInsert
        {
            public int BrokerId { get; set; }
            public int[] MasterIds { get; set; } = Array.Empty<int>();
            public string City { get; set; } = "Indore";
            public string LocationResolutionStatus { get; set; } = "missing";
            public string? LocationResolutionNote { get; set; }
            public string RequirementType { get; set; } = "BUY";
            public string? PropertyType { get; set; }
            public string[] Configurations { get; set; } = Array.Empty<string>();
            public decimal? Budget { get; set; }
            public string? BudgetUnit { get; set; }
            public decimal? Size { get; set; }
            public string? FurnishingPref { get; set; }
            public string? FacingPref { get; set; }
            public string? RawMessageText { get; set; }
            public string? ContentHash { get; set; }
            public string? GroupName { get; set; }
            public string? MessageDateTime { get; set; }
        }
    }

    // ═════════════════════════════════════════════════════════
    //  RESULT MODEL (public — used by Function.cs response)
    //  Place this alongside EmbedResult in the namespace.
    // ═════════════════════════════════════════════════════════

    public class IngestResult
    {
        public int ListingsInserted { get; set; }
        public int RequirementsInserted { get; set; }
        public int BrokersCreated { get; set; }
        public int LocalitiesCreated { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public string? FirstFailure { get; set; }
    }
}
