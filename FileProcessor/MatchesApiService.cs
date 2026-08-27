// ============================================================
// FILE: MatchesApiService.cs
// ============================================================
// Uses fn_get_filtered_matches PostgreSQL function for filtered queries.
// Keeps legacy broker/listing/requirement filters for backward compatibility.
//
// NEW FILTER PARAMETERS:
//   GET /matches?listingType=SELL,RENT
//   GET /matches?requirementType=BUY
//   GET /matches?matchStatus=MATCHED,CONFIRMED
//   GET /matches?locations=Vijay Nagar,Nipania
//   GET /matches?minBudget=1000000&maxBudget=50000000
//   GET /matches?searchText=plot near hospital
//
// LEGACY PARAMETERS (still supported):
//   GET /matches?broker_id=99
//   GET /matches?listing_id=42
//   GET /matches?requirement_id=5
// ============================================================

using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Npgsql;
using NpgsqlTypes;
using Pgvector.Npgsql;
using System.Text.Json;

namespace propseekr_file_processor
{
    public class MatchesApiService
    {
        private readonly NpgsqlDataSource _dataSource;

        public MatchesApiService(string dbConnectionString)
        {
            var builder = new NpgsqlDataSourceBuilder(dbConnectionString);
            builder.UseVector();
            _dataSource = builder.Build();
        }

        public async Task<APIGatewayProxyResponse> HandleGetMatchesAsync(
            APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                var q = request.QueryStringParameters ?? new Dictionary<string, string>();

                int page = GetInt(q, "page", 1);
                int size = GetInt(q, "size", 20);
                string[]? listingTypes = GetStringArray(q, "listingType");
                string[]? requirementTypes = GetStringArray(q, "requirementType");
                string[]? matchStatuses = GetStringArray(q, "matchStatus");
                string[]? locations = GetStringArray(q, "locations");
                decimal? minBudget = GetNullableDecimal(q, "minBudget");
                decimal? maxBudget = GetNullableDecimal(q, "maxBudget");
                string? searchText = GetString(q, "searchText");
                int? brokerId = GetNullableInt(q, "broker_id");
                int? listingId = GetNullableInt(q, "listing_id");
                int? requirementId = GetNullableInt(q, "requirement_id");

                if (page < 1) page = 1;
                if (size < 1 || size > 100) size = 20;

                context.Logger.LogInformation(
                    $"GET /matches page={page} size={size} " +
                    $"listingType={listingTypes?.Length ?? 0} reqType={requirementTypes?.Length ?? 0} " +
                    $"locations={locations?.Length ?? 0} search='{searchText}' " +
                    $"broker={brokerId} listing={listingId} req={requirementId}");

                await using var conn = await _dataSource.OpenConnectionAsync();

                MatchesResponse result;

                if (brokerId.HasValue)
                    result = await QueryBrokerMatches(conn, brokerId.Value, page, size);
                else if (listingId.HasValue)
                    result = await QueryByListingOrRequirement(conn, "listing", listingId.Value, page, size);
                else if (requirementId.HasValue)
                    result = await QueryByListingOrRequirement(conn, "requirement", requirementId.Value, page, size);
                else
                    result = await QueryFilteredMatches(conn, page, size,
                        listingTypes, requirementTypes, matchStatuses,
                        locations, minBudget, maxBudget, searchText);

                return Respond(200, result);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"GET /matches error: {ex}");
                return Respond(500, new { error = "Failed to fetch matches", detail = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────
        //  FILTERED MATCHES (uses fn_get_filtered_matches)
        // ─────────────────────────────────────────────────────

        private static async Task<MatchesResponse> QueryFilteredMatches(
            NpgsqlConnection conn, int page, int size,
            string[]? listingTypes, string[]? requirementTypes,
            string[]? matchStatuses, string[]? locations,
            decimal? minBudget, decimal? maxBudget, string? searchText)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT * FROM fn_get_filtered_matches(@p_page, @p_size, @p_listing_types, @p_requirement_types, @p_match_statuses, @p_locations, @p_min_budget, @p_max_budget, @p_search_text)",
                conn);

            cmd.Parameters.AddWithValue("p_page", page);
            cmd.Parameters.AddWithValue("p_size", size);

            var ltParam = cmd.Parameters.Add("p_listing_types", NpgsqlDbType.Array | NpgsqlDbType.Text);
            ltParam.Value = (object?)listingTypes ?? DBNull.Value;

            var rtParam = cmd.Parameters.Add("p_requirement_types", NpgsqlDbType.Array | NpgsqlDbType.Text);
            rtParam.Value = (object?)requirementTypes ?? DBNull.Value;

            var msParam = cmd.Parameters.Add("p_match_statuses", NpgsqlDbType.Array | NpgsqlDbType.Text);
            msParam.Value = (object?)matchStatuses ?? DBNull.Value;

            var locParam = cmd.Parameters.Add("p_locations", NpgsqlDbType.Array | NpgsqlDbType.Text);
            locParam.Value = (object?)locations ?? DBNull.Value;

            cmd.Parameters.AddWithValue("p_min_budget", (object?)minBudget ?? DBNull.Value);
            cmd.Parameters.AddWithValue("p_max_budget", (object?)maxBudget ?? DBNull.Value);
            cmd.Parameters.AddWithValue("p_search_text", (object?)searchText ?? DBNull.Value);

            cmd.CommandTimeout = 120;

            var matches = new List<object>();
            long totalMatches = 0;
            int currentPage = page;
            int totalPages = 0;

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                totalMatches = reader.GetInt64(reader.GetOrdinal("total_matches"));
                currentPage = reader.GetInt32(reader.GetOrdinal("current_page"));
                totalPages = reader.GetInt32(reader.GetOrdinal("total_pages"));

                matches.Add(new
                {
                    MatchQuality = Safe(reader, "match_quality"),
                    ScorePercent = reader.GetInt32(reader.GetOrdinal("score_pct")),

                    Property = new
                    {
                        For = Safe(reader, "property_for"),
                        Type = Safe(reader, "property_type"),
                        Config = Safe(reader, "config", "-"),
                        Price = Safe(reader, "property_price"),
                        Size = Safe(reader, "property_size", "-"),
                        Location = Safe(reader, "property_location", "-"),
                        City = Safe(reader, "property_city", "-"),
                        BrokerName = Safe(reader, "seller_broker", "-"),
                        BrokerPhone = Safe(reader, "seller_phone", "-"),
                        GroupName = Safe(reader, "listing_group_name", "-"),
                        MessageDateTime = Safe(reader, "listing_message_datetime", "-"),
                        RawText = Safe(reader, "listing_raw_text", "-")
                    },

                    Buyer = new
                    {
                        LookingFor = Safe(reader, "looking_for"),
                        Type = Safe(reader, "buyer_wants"),
                        Budget = Safe(reader, "buyer_budget"),
                        Size = Safe(reader, "buyer_size", "-"),
                        Location = Safe(reader, "buyer_location", "-"),
                        City = Safe(reader, "buyer_city", "-"),
                        BrokerName = Safe(reader, "buyer_broker", "-"),
                        BrokerPhone = Safe(reader, "buyer_phone", "-"),
                        GroupName = Safe(reader, "requirement_group_name", "-"),
                        MessageDateTime = Safe(reader, "requirement_message_datetime", "-"),
                        RawText = Safe(reader, "requirement_raw_text", "-")
                    },

                    MatchDetails = new
                    {
                        Location = Safe(reader, "location_match"),
                        Price = Safe(reader, "price_match"),
                        Size = Safe(reader, "size_match")
                    }
                });
            }

            return new MatchesResponse
            {
                Matches = matches,
                Pagination = new
                {
                    CurrentPage = currentPage,
                    PageSize = size,
                    TotalMatches = totalMatches,
                    TotalPages = totalPages
                }
            };
        }

        // ─────────────────────────────────────────────────────
        //  BROKER MATCHES (legacy)
        // ─────────────────────────────────────────────────────

        private static async Task<MatchesResponse> QueryBrokerMatches(
            NpgsqlConnection conn, int brokerId, int page, int pageSize)
        {
            int offset = (page - 1) * pageSize;

            await using var countCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM matches 
                WHERE status = 'MATCHED' 
                  AND (listing_broker_id = @bid OR requirement_broker_id = @bid)", conn);
            countCmd.Parameters.AddWithValue("bid", brokerId);
            long totalCount = (long)(await countCmd.ExecuteScalarAsync())!;

            await using var cmd = new NpgsqlCommand(@"
                SELECT
                    m.matchid, m.match_score, m.score_breakdown,
                    l.listing_type, l.property_type, l.configuration,
                    l.price, l.price_unit, l.size,
                    l.furnishing, l.facing, l.freshness_category,
                    ms.area, ms.city,
                    lb.name, lb.phone_number,
                    r.requirement_type, r.property_type, r.configurations,
                    r.budget, r.budget_unit, r.size,
                    r.furnishing_pref, r.facing_pref,
                    rm.area, rm.city,
                    rb.name, rb.phone_number,
                    m.listing_broker_id, m.requirement_broker_id,
                    l.group_name, l.message_datetime, l.raw_message_text,
                    r.group_name, r.message_datetime, r.raw_message_text
                FROM matches m
                JOIN listings l ON l.listingid = m.listing_id
                JOIN requirements r ON r.requirementid = m.requirement_id
                JOIN brokers lb ON lb.brokerid = m.listing_broker_id
                JOIN brokers rb ON rb.brokerid = m.requirement_broker_id
                LEFT JOIN master ms ON ms.masterid = l.master_id
                LEFT JOIN master rm ON rm.masterid = r.preferred_locality_ids[1]
                WHERE m.status = 'MATCHED'
                  AND (m.listing_broker_id = @bid OR m.requirement_broker_id = @bid)
                ORDER BY m.match_score DESC
                OFFSET @offset LIMIT @limit", conn);

            cmd.Parameters.AddWithValue("bid", brokerId);
            cmd.Parameters.AddWithValue("offset", offset);
            cmd.Parameters.AddWithValue("limit", pageSize);

            var matches = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                JsonElement? breakdown = reader.IsDBNull(2) ? null :
                    (JsonElement?)JsonDocument.Parse(reader.GetString(2)).RootElement;

                int listingBrokerId = reader.GetInt32(28);
                int reqBrokerId = reader.GetInt32(29);

                string yourRole = (listingBrokerId == brokerId && reqBrokerId == brokerId)
                    ? "Both sides"
                    : listingBrokerId == brokerId
                        ? "You have the property"
                        : "You have the buyer";

                matches.Add(BuildMatchRow(reader, breakdown, yourRole));
            }

            return new MatchesResponse
            {
                Matches = matches,
                Pagination = BuildPagination(page, pageSize, totalCount),
                Filter = $"broker_id={brokerId}"
            };
        }

        // ─────────────────────────────────────────────────────
        //  LISTING / REQUIREMENT MATCHES (legacy)
        // ─────────────────────────────────────────────────────

        private static async Task<MatchesResponse> QueryByListingOrRequirement(
            NpgsqlConnection conn, string mode, int id, int page, int pageSize)
        {
            int offset = (page - 1) * pageSize;
            string whereClause = mode == "listing"
                ? "m.listing_id = @id"
                : "m.requirement_id = @id";

            await using var countCmd = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM matches m WHERE m.status = 'MATCHED' AND {whereClause}", conn);
            countCmd.Parameters.AddWithValue("id", id);
            long totalCount = (long)(await countCmd.ExecuteScalarAsync())!;

            await using var cmd = new NpgsqlCommand($@"
                SELECT
                    m.matchid, m.match_score, m.score_breakdown,
                    l.listing_type, l.property_type, l.configuration,
                    l.price, l.price_unit, l.size,
                    l.furnishing, l.facing, l.freshness_category,
                    ms.area, ms.city,
                    lb.name, lb.phone_number,
                    r.requirement_type, r.property_type, r.configurations,
                    r.budget, r.budget_unit, r.size,
                    r.furnishing_pref, r.facing_pref,
                    rm.area, rm.city,
                    rb.name, rb.phone_number,
                    l.group_name, l.message_datetime, l.raw_message_text,
                    r.group_name, r.message_datetime, r.raw_message_text
                FROM matches m
                JOIN listings l ON l.listingid = m.listing_id
                JOIN requirements r ON r.requirementid = m.requirement_id
                JOIN brokers lb ON lb.brokerid = m.listing_broker_id
                JOIN brokers rb ON rb.brokerid = m.requirement_broker_id
                LEFT JOIN master ms ON ms.masterid = l.master_id
                LEFT JOIN master rm ON rm.masterid = r.preferred_locality_ids[1]
                WHERE m.status = 'MATCHED' AND {whereClause}
                ORDER BY m.match_score DESC
                OFFSET @offset LIMIT @limit", conn);

            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("offset", offset);
            cmd.Parameters.AddWithValue("limit", pageSize);

            var matches = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                JsonElement? breakdown = reader.IsDBNull(2) ? null :
                    (JsonElement?)JsonDocument.Parse(reader.GetString(2)).RootElement;

                matches.Add(BuildMatchRow(reader, breakdown, null));
            }

            return new MatchesResponse
            {
                Matches = matches,
                Pagination = BuildPagination(page, pageSize, totalCount),
                Filter = $"{mode}_id={id}"
            };
        }

        // ─────────────────────────────────────────────────────
        //  ROW BUILDER (for legacy queries)
        // ─────────────────────────────────────────────────────

        private static object BuildMatchRow(
            NpgsqlDataReader r, JsonElement? breakdown, string? yourRole)
        {
            var score = r.GetDecimal(1);

            return new
            {
                MatchQuality = score >= 70 ? "Excellent Match"
                    : score >= 55 ? "Good Match"
                    : score >= 40 ? "Decent Match"
                    : "Possible Match",
                ScorePercent = (int)Math.Round(score),

                Property = new
                {
                    For = SafeOrd(r, 3) switch
                    {
                        "SELL" => "For Sale",
                        "RENT" => "For Rent",
                        "LEASE" => "For Lease",
                        var v => v
                    },
                    Type = FormatPropertyType(SafeOrd(r, 4)),
                    Config = SafeOrd(r, 5, "-"),
                    Price = FormatPrice(GetDecOrd(r, 6), SafeOrd(r, 7)),
                    Size = FormatSize(GetDecOrd(r, 8)),
                    Furnishing = FormatFurnishing(SafeOrd(r, 9)),
                    Facing = SafeOrd(r, 10, "-"),
                    Freshness = SafeOrd(r, 11) switch
                    {
                        "RECENTLY_CONFIRMED" => "Verified",
                        "FRESH" => "Fresh",
                        "MODERATE" => "Moderate",
                        "OLD" => "Getting old",
                        "EXPIRED" => "Expired",
                        _ => "Fresh"
                    },
                    Location = SafeOrd(r, 12, "-"),
                    City = SafeOrd(r, 13, "-"),
                    BrokerName = SafeOrd(r, 14, "-"),
                    BrokerPhone = SafeOrd(r, 15, "-"),
                    GroupName = SafeOrd(r, 30, "-"),
                    MessageDateTime = SafeOrd(r, 31, "-"),
                    RawText = SafeOrd(r, 32, "-")
                },

                Buyer = new
                {
                    LookingFor = SafeOrd(r, 16) switch
                    {
                        "BUY" => "Wants to Buy",
                        "RENT" => "Wants to Rent",
                        "LEASE" => "Wants to Lease",
                        var v => v
                    },
                    Type = FormatPropertyType(SafeOrd(r, 17)),
                    Config = FormatConfigurations(SafeOrd(r, 18)),
                    Budget = FormatPrice(GetDecOrd(r, 19), SafeOrd(r, 20)),
                    Size = FormatSize(GetDecOrd(r, 21)),
                    Furnishing = FormatFurnishing(SafeOrd(r, 22)),
                    Facing = SafeOrd(r, 23, "Any"),
                    Location = SafeOrd(r, 24, "Any location"),
                    City = SafeOrd(r, 25, "-"),
                    BrokerName = SafeOrd(r, 26, "-"),
                    BrokerPhone = SafeOrd(r, 27, "-"),
                    GroupName = SafeOrd(r, 33, "-"),
                    MessageDateTime = SafeOrd(r, 34, "-"),
                    RawText = SafeOrd(r, 35, "-")
                },

                MatchDetails = new
                {
                    Location = GetMatchIcon(breakdown, "is_exact", "distance_km"),
                    Price = GetPriceIcon(breakdown),
                    Size = GetSizeIcon(breakdown)
                },

                YourRole = yourRole
            };
        }

        // ─────────────────────────────────────────────────────
        //  FORMATTING HELPERS
        // ─────────────────────────────────────────────────────

        private static string FormatPrice(decimal? price, string? unit)
        {
            if (!price.HasValue) return "Price on request";
            var p = price.Value;
            string suffix = unit switch
            {
                "PER_SQFT" => "/sqft",
                "PER_MONTH" => "/month",
                "PER_BIGHA" => "/bigha",
                "PER_ACRE" => "/acre",
                _ => ""
            };
            if (suffix == "" || suffix == "/bigha" || suffix == "/acre")
            {
                if (p >= 10_000_000m) return $"₹{Math.Round(p / 10_000_000m, 2)} Cr{suffix}";
                if (p >= 100_000m) return $"₹{Math.Round(p / 100_000m, 2)} Lakh{suffix}";
            }
            return $"₹{p:N0}{suffix}";
        }

        private static string FormatSize(decimal? size)
        {
            if (!size.HasValue) return "-";
            var s = size.Value;
            if (s >= 43560) return $"{Math.Round(s / 43560m, 2)} acre";
            if (s >= 12000) return $"{Math.Round(s / 12000m, 2)} bigha";
            return $"{s:N0} sqft";
        }

        private static string FormatPropertyType(string? raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "-") return "-";
            return System.Globalization.CultureInfo.InvariantCulture.TextInfo
                .ToTitleCase(raw.Replace("_", " ").ToLowerInvariant());
        }

        private static string FormatFurnishing(string? raw) => raw switch
        {
            "FURNISHED" => "Furnished",
            "SEMI" => "Semi-Furnished",
            "BARE" => "Unfurnished",
            _ => "-"
        };

        private static string FormatConfigurations(string? raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "-") return "Any";
            return raw.Trim('{', '}').Replace(",", ", ");
        }

        private static string GetMatchIcon(JsonElement? bd, string exactKey, string distKey)
        {
            if (bd == null) return "Nearby area";
            var el = bd.Value;
            if (el.TryGetProperty(exactKey, out var exact) && exact.ValueKind == JsonValueKind.True)
                return "Same area";
            if (el.TryGetProperty(distKey, out var dist) && dist.ValueKind == JsonValueKind.Number)
            {
                var km = dist.GetDecimal();
                if (km <= 1) return "Within 1 km";
                if (km <= 2) return "Within 2 km";
                return "Within 3 km";
            }
            return "Nearby area";
        }

        private static string GetPriceIcon(JsonElement? bd)
        {
            if (bd == null) return "Unknown";
            if (bd.Value.TryGetProperty("price_score", out var ps))
            {
                var s = ps.GetDecimal();
                if (s >= 25) return "Within budget";
                if (s >= 15) return "Slightly over budget";
                if (s == 12) return "Can't compare";
                if (s >= 8) return "Over budget (up to 10%)";
                return "Over budget";
            }
            return "Unknown";
        }

        private static string GetSizeIcon(JsonElement? bd)
        {
            if (bd == null) return "Unknown";
            if (bd.Value.TryGetProperty("size_score", out var ss))
            {
                var s = ss.GetDecimal();
                if (s >= 15) return "Size matches";
                if (s >= 8) return "Size approximate";
                return "Size mismatch";
            }
            return "Unknown";
        }

        // ─────────────────────────────────────────────────────
        //  READER HELPERS
        // ─────────────────────────────────────────────────────

        private static string Safe(NpgsqlDataReader r, string col, string fallback = "")
        {
            var ord = r.GetOrdinal(col);
            return r.IsDBNull(ord) ? fallback : r.GetValue(ord)?.ToString() ?? fallback;
        }

        private static string SafeOrd(NpgsqlDataReader r, int ord, string fallback = "")
        {
            return (ord >= r.FieldCount || r.IsDBNull(ord)) ? fallback : r.GetValue(ord)?.ToString() ?? fallback;
        }

        private static decimal? GetDecOrd(NpgsqlDataReader r, int ord)
        {
            return r.IsDBNull(ord) ? null : r.GetDecimal(ord);
        }

        private static int GetInt(IDictionary<string, string> q, string k, int d)
            => q.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : d;

        private static int? GetNullableInt(IDictionary<string, string> q, string k)
            => q.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : null;

        private static decimal? GetNullableDecimal(IDictionary<string, string> q, string k)
            => q.TryGetValue(k, out var v) && decimal.TryParse(v, out var n) ? n : null;

        private static string? GetString(IDictionary<string, string> q, string k)
            => q.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        private static string[]? GetStringArray(IDictionary<string, string> q, string k)
        {
            if (!q.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)) return null;
            var arr = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return arr.Length > 0 ? arr : null;
        }

        private static object BuildPagination(int page, int size, long total) => new
        {
            CurrentPage = page,
            PageSize = size,
            TotalMatches = total,
            TotalPages = (int)Math.Ceiling((double)total / size)
        };

        private static APIGatewayProxyResponse Respond(int status, object body) =>
            new APIGatewayProxyResponse
            {
                StatusCode = status,
                Body = JsonSerializer.Serialize(body, new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "Access-Control-Allow-Origin", "*" },
                    { "Access-Control-Allow-Methods", "GET, OPTIONS" },
                    { "Access-Control-Allow-Headers", "Content-Type" }
                }
            };
    }

    public class MatchesResponse
    {
        public List<object> Matches { get; set; } = new();
        public object? Pagination { get; set; }
        public string? Filter { get; set; }
    }
}
