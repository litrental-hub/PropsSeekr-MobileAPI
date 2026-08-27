// ============================================================
// FILE: ListingFormService.cs
// ============================================================
// Handles single listing/requirement submission from a UI form.
// Reuses IngestService internally for all DB operations.
//
// INTEGRATION (in Function.cs):
//
// 1. Add field:
//    private readonly ListingFormService _listingForm;
//
// 2. Add to constructor:
//    _listingForm = new ListingFormService(_ingestService);
//
// 3. Add route in FunctionHandler (before /upload):
//    if (path.EndsWith("/listing"))
//        return await _listingForm.HandleSubmitAsync(request, context);
//
// 4. Add API Gateway resource: POST /listing → same Lambda
//
// ENDPOINTS:
//   POST /listing  → submit a single listing or requirement from form
// ============================================================

using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using System.Text;
using System.Text.Json;

namespace propseekr_file_processor
{
    public class ListingFormService
    {
        private readonly IngestService _ingestService;
        private readonly Func<ILambdaContext, Task> _runEmbedAndMatchingAsync;

        public ListingFormService(
            IngestService ingestService,
            Func<ILambdaContext, Task> runEmbedAndMatchingAsync)
        {
            _ingestService = ingestService;
            _runEmbedAndMatchingAsync = runEmbedAndMatchingAsync;
        }

        /// <summary>
        /// POST /listing
        /// Accepts a single property listing from a UI form and saves to DB.
        ///
        /// Request body — same format as PropertyListing:
        /// {
        ///     "listingType": "Sale",           // Sale, Rent, Requirement
        ///     "propertyType": "Plot",           // Plot, Flat, Land, Villa, etc.
        ///     "configuration": "2BHK",          // optional
        ///     "location": "Super Corridor Indore",
        ///     "projectName": "",                // optional
        ///     "size": [1200],                   // array of sizes
        ///     "sizeUnit": "sqft",               // sqft, bigha, acre
        ///     "price": 4500000,                 // optional
        ///     "priceUnit": "Total",             // Total, PerSqFt, PerMonth
        ///     "pricePerUnit": null,             // optional (use if priceUnit is PerSqFt)
        ///     "facing": "North",                // optional
        ///     "roadInfo": "Corner Plot",         // optional
        ///     "furnishing": "Furnished",         // optional
        ///     "contactName": "Gaurav Verma",
        ///     "contactNumber": "7354844413",
        ///     "description": "Plot for sale..."  // free text description
        /// }
        /// </summary>
        public async Task<APIGatewayProxyResponse> HandleSubmitAsync(
            APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                var rawBody = request.Body ?? "";
                var body = request.IsBase64Encoded
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(rawBody))
                    : rawBody;

                if (string.IsNullOrWhiteSpace(body) || body == "-")
                    return Respond(400, new { error = "Send property details as JSON" });

                context.Logger.LogInformation($"POST /listing body length: {body.Length}");

                using var jdoc = JsonDocument.Parse(body);
                var root = jdoc.RootElement;

                // Validate required fields
                var contactNumber = GetStr(root, "contactNumber");
                var contactName = GetStr(root, "contactName");
                var listingType = GetStr(root, "listingType");

                if (string.IsNullOrWhiteSpace(contactNumber))
                    return Respond(400, new { error = "contactNumber is required" });

                if (string.IsNullOrWhiteSpace(listingType))
                    return Respond(400, new { error = "listingType is required (Sale, Rent, or Requirement)" });

                // Build description from form fields for RawText
                var description = GetStr(root, "description");
                if (string.IsNullOrWhiteSpace(description))
                {
                    description = BuildDescriptionFromForm(root);
                }

                // Map form JSON to PropertyListing (same model used by IngestService)
                var listing = new PropertyListing
                {
                    SenderName = contactName,
                    MessageDate = DateTime.UtcNow.ToString("dd/MM/yy"),
                    ListingType = listingType,
                    PropertyType = GetStr(root, "propertyType"),
                    Configuration = GetStr(root, "configuration"),
                    Location = GetStr(root, "location"),
                    ProjectName = GetStr(root, "projectName"),
                    Size = GetDecimalList(root, "size"),
                    SizeUnit = GetStr(root, "sizeUnit"),
                    Width = GetDec(root, "width"),
                    Length = GetDec(root, "length"),
                    Price = GetDec(root, "price"),
                    PriceUnit = GetStr(root, "priceUnit"),
                    PricePerUnit = GetDec(root, "pricePerUnit"),
                    Facing = GetStr(root, "facing"),
                    RoadInfo = GetStr(root, "roadInfo"),
                    Furnishing = GetStr(root, "furnishing"),
                    ContactName = contactName,
                    ContactNumber = contactNumber,
                    RawText = description
                };

                // Use IngestService to process this single listing
                // Wrap in a list since IngestService expects a list
                var listings = new List<PropertyListing> { listing };

                var result = await _ingestService.ProcessSingleFormListing(listings, context.Logger);


                // Trigger embed + matching asynchronously (non-blocking)
                if (result.ListingsInserted > 0 || result.RequirementsInserted > 0)
                {
                    try
                    {
                        await _runEmbedAndMatchingAsync(context);
                        context.Logger.LogInformation("Local embed + matching completed");
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogError($"Local embed + matching failed: {ex.Message}");
                    }
                }

                context.Logger.LogInformation(
                    $"Form submit: listings={result.ListingsInserted}, " +
                    $"requirements={result.RequirementsInserted}, " +
                    $"brokers={result.BrokersCreated}");

                return Respond(200, new
                {
                    message = result.ListingsInserted > 0
                        ? "Property listing saved successfully. Embedding and matching started."
                        : result.RequirementsInserted > 0
                            ? "Requirement saved successfully. Embedding and matching started."
                            : "No records saved (possible duplicate or invalid data)",
                    listingsInserted = result.ListingsInserted,
                    requirementsInserted = result.RequirementsInserted,
                    brokersCreated = result.BrokersCreated,
                    localitiesCreated = result.LocalitiesCreated,
                    skipped = result.Skipped,
                    failed = result.Failed
                });
            }
            catch (JsonException ex)
            {
                return Respond(400, new { error = "Invalid JSON", detail = ex.Message });
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Form submit error: {ex}");
                return Respond(500, new { error = "Failed to save listing", detail = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────
        //  Build a description from form fields (for RawText)
        // ─────────────────────────────────────────────────────

        private static string BuildDescriptionFromForm(JsonElement root)
        {
            var parts = new List<string>();

            var propType = GetStr(root, "propertyType");
            var listingType = GetStr(root, "listingType");
            var config = GetStr(root, "configuration");
            var location = GetStr(root, "location");
            var size = GetStr(root, "size");
            var sizeUnit = GetStr(root, "sizeUnit");
            var price = GetStr(root, "price");
            var priceUnit = GetStr(root, "priceUnit");
            var facing = GetStr(root, "facing");
            var furnishing = GetStr(root, "furnishing");
            var roadInfo = GetStr(root, "roadInfo");

            if (!string.IsNullOrEmpty(config)) parts.Add(config);
            if (!string.IsNullOrEmpty(propType)) parts.Add(propType);

            if (listingType.Equals("Sale", StringComparison.OrdinalIgnoreCase))
                parts.Add("for sale");
            else if (listingType.Equals("Rent", StringComparison.OrdinalIgnoreCase))
                parts.Add("for rent");
            else if (listingType.Equals("Requirement", StringComparison.OrdinalIgnoreCase))
                parts.Add("required");

            if (!string.IsNullOrEmpty(location)) parts.Add($"in {location}");
            if (!string.IsNullOrEmpty(size)) parts.Add($"size {size} {sizeUnit ?? "sqft"}");
            if (!string.IsNullOrEmpty(price)) parts.Add($"price {price} {priceUnit ?? ""}".Trim());
            if (!string.IsNullOrEmpty(facing)) parts.Add($"{facing} facing");
            if (!string.IsNullOrEmpty(furnishing)) parts.Add(furnishing);
            if (!string.IsNullOrEmpty(roadInfo)) parts.Add(roadInfo);

            return string.Join(" ", parts);
        }

        // ─────────────────────────────────────────────────────
        //  JSON HELPERS
        // ─────────────────────────────────────────────────────

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

        private static decimal? GetDec(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String &&
                decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
            if (v.ValueKind == JsonValueKind.Null) return null;
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
                }
                return list.Count > 0 ? list : null;
            }
            return null;
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
