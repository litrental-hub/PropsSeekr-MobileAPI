using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;
using Npgsql;

namespace propseekr_file_processor;

public sealed record GeocodingResult(
    string Status,
    decimal? Latitude,
    decimal? Longitude,
    string Provider,
    string? PlaceId,
    string? FormattedAddress,
    string? Precision,
    decimal Confidence,
    string? Error)
{
    public bool IsResolved => Status == "resolved" && Latitude.HasValue && Longitude.HasValue;
}

/// <summary>
/// Server-side Google Geocoding API client. Results must belong to the expected
/// city and represent the requested area rather than a generic city centre.
/// </summary>
public sealed class GeocodingService : IDisposable
{
    private const decimal AutoAcceptConfidence = 0.70m;
    private readonly HttpClient _http = new();
    private readonly string? _apiKey;
    private readonly Dictionary<string, GeocodingResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTime _lastRequestTime = DateTime.MinValue;

    public GeocodingService()
    {
        _apiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PropSeekr/2.0 (server-geocoding)");
    }

    public async Task<(decimal lat, decimal lng)?> GeocodeAsync(
        string area,
        string city,
        CancellationToken cancellationToken = default)
    {
        var result = await GeocodeDetailedAsync(area, city, cancellationToken);
        return result.IsResolved ? (result.Latitude!.Value, result.Longitude!.Value) : null;
    }

    public async Task<GeocodingResult> GeocodeDetailedAsync(
        string area,
        string city,
        CancellationToken cancellationToken = default)
    {
        area = area?.Trim() ?? string.Empty;
        city = CityExtractor.NormalizeDefaultCity(city);
        var cacheKey = $"{Normalize(area)}|{Normalize(city)}";
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        if (string.IsNullOrWhiteSpace(_apiKey))
            return Cache(cacheKey, Failure("configuration_error", "Google server geocoding is not configured."));
        if (string.IsNullOrWhiteSpace(area))
            return Cache(cacheKey, Failure("review_required", "No locality text was supplied."));

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequestTime;
            if (elapsed.TotalMilliseconds < 75)
                await Task.Delay(75 - (int)elapsed.TotalMilliseconds, cancellationToken);
            _lastRequestTime = DateTime.UtcNow;

            var address = string.Join(", ", new[] { area, city, StateForCity(city), "India" }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var url = "https://maps.googleapis.com/maps/api/geocode/json" +
                      $"?address={HttpUtility.UrlEncode(address)}" +
                      "&components=country%3AIN&region=in" +
                      $"&key={HttpUtility.UrlEncode(_apiKey)}";
            using var response = await _http.GetAsync(url, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Cache(cacheKey, Failure("provider_error", $"Google returned HTTP {(int)response.StatusCode}."));

            var envelope = JsonSerializer.Deserialize<GoogleEnvelope>(payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (envelope == null || !string.Equals(envelope.Status, "OK", StringComparison.OrdinalIgnoreCase) ||
                envelope.Results is not { Count: > 0 })
            {
                var message = string.IsNullOrWhiteSpace(envelope?.ErrorMessage)
                    ? $"Google geocoding status: {envelope?.Status ?? "invalid_response"}."
                    : envelope.ErrorMessage;
                return Cache(cacheKey, Failure(
                    string.Equals(envelope?.Status, "ZERO_RESULTS", StringComparison.OrdinalIgnoreCase)
                        ? "review_required"
                        : "provider_error",
                    message));
            }

            var best = envelope.Results.Select(result => Score(result, area, city))
                .OrderByDescending(candidate => candidate.Confidence)
                .First();
            if (best.Result.Geometry?.Location == null)
                return Cache(cacheKey, Failure("provider_error", "Google returned a result without coordinates."));

            var resolved = best.CityMatches && best.Confidence >= AutoAcceptConfidence;
            return Cache(cacheKey, new GeocodingResult(
                resolved ? "resolved" : "review_required",
                resolved ? best.Result.Geometry.Location.Lat : null,
                resolved ? best.Result.Geometry.Location.Lng : null,
                "google",
                Truncate(best.Result.PlaceId, 255),
                Truncate(best.Result.FormattedAddress, 500),
                Truncate(best.Result.Geometry.LocationType, 40),
                best.Confidence,
                resolved ? null : "The provider result was not precise enough or did not match the expected city."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Cache(cacheKey, Failure("provider_error", ex.Message));
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async Task<int> BackfillMasterCoordinatesAsync(
        NpgsqlConnection connection,
        int maximumRows = 25,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<(int Id, string Area, string City)>();
        await using (var command = new NpgsqlCommand("""
            SELECT masterid, area, city
            FROM master
            WHERE (lat IS NULL OR lng IS NULL)
              AND COALESCE(geocoding_status, 'pending') IN ('pending', 'provider_error')
            ORDER BY masterid
            LIMIT @maximum_rows
            """, connection))
        {
            command.Parameters.AddWithValue("maximum_rows", maximumRows);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        var updated = 0;
        foreach (var row in rows)
        {
            var result = await GeocodeDetailedAsync(row.Area, row.City, cancellationToken);
            await using var update = new NpgsqlCommand("""
                UPDATE master
                SET lat = @lat, lng = @lng, geocoding_status = @status,
                    geocoding_provider = @provider, provider_place_id = @place_id,
                    formatted_address = @formatted_address, location_precision = @precision,
                    geocoding_confidence = @confidence, geocoded_at = NOW(),
                    geocoding_error = @error, review_required = @review_required
                WHERE masterid = @id
                """, connection);
            update.Parameters.AddWithValue("id", row.Id);
            update.Parameters.AddWithValue("lat", (object?)result.Latitude ?? DBNull.Value);
            update.Parameters.AddWithValue("lng", (object?)result.Longitude ?? DBNull.Value);
            update.Parameters.AddWithValue("status", result.Status);
            update.Parameters.AddWithValue("provider", result.Provider);
            update.Parameters.AddWithValue("place_id", (object?)result.PlaceId ?? DBNull.Value);
            update.Parameters.AddWithValue("formatted_address", (object?)result.FormattedAddress ?? DBNull.Value);
            update.Parameters.AddWithValue("precision", (object?)result.Precision ?? DBNull.Value);
            update.Parameters.AddWithValue("confidence", result.Confidence);
            update.Parameters.AddWithValue("error", (object?)Truncate(result.Error, 1000) ?? DBNull.Value);
            update.Parameters.AddWithValue("review_required", !result.IsResolved);
            await update.ExecuteNonQueryAsync(cancellationToken);
            if (result.IsResolved) updated++;
        }
        return updated;
    }

    private static ScoredResult Score(GoogleResult result, string area, string city)
    {
        var normalizedCity = NormalizeCityAlias(city);
        var cityValues = result.AddressComponents
            .Where(component => component.Types.Any(type => type is "locality" or "postal_town" or "administrative_area_level_2" or "administrative_area_level_3"))
            .SelectMany(component => new[] { component.LongName, component.ShortName })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeCityAlias(value!))
            .ToArray();
        var normalizedAddress = Normalize(result.FormattedAddress);
        var cityMatches = cityValues.Any(value => value == normalizedCity || value.Contains(normalizedCity) || normalizedCity.Contains(value))
                          || normalizedAddress.Contains(normalizedCity);

        var resultTypes = result.Types.Concat(result.AddressComponents.SelectMany(component => component.Types))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        decimal confidence = cityMatches ? 0.35m : 0m;
        if (resultTypes.Overlaps(new[] { "street_address", "premise", "subpremise", "neighborhood", "sublocality", "sublocality_level_1", "route" }))
            confidence += 0.35m;
        else if (resultTypes.Contains("locality")) confidence += 0.15m;

        confidence += result.Geometry?.LocationType?.ToUpperInvariant() switch
        {
            "ROOFTOP" => 0.20m,
            "RANGE_INTERPOLATED" => 0.17m,
            "GEOMETRIC_CENTER" => 0.12m,
            _ => 0.05m
        };

        var normalizedArea = Normalize(area);
        var meaningfulTokens = normalizedArea.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3 && token is not "road" and not "nagar" and not "near")
            .ToArray();
        if (normalizedAddress.Contains(normalizedArea) || meaningfulTokens.Any(normalizedAddress.Contains))
            confidence += 0.20m;
        return new ScoredResult(result, cityMatches, Math.Clamp(confidence, 0m, 1m));
    }

    private static string Normalize(string? value) => Regex.Replace(
        (value ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static string NormalizeCityAlias(string value) => Normalize(value) switch
    {
        "bangalore" => "bengaluru",
        "gurgaon" => "gurugram",
        "bombay" => "mumbai",
        _ => Normalize(value)
    };

    private GeocodingResult Cache(string key, GeocodingResult result)
    {
        _cache[key] = result;
        return result;
    }

    private static GeocodingResult Failure(string status, string error) =>
        new(status, null, null, "google", null, null, null, 0m, Truncate(error, 1000));
    private static string? Truncate(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= length ? value : value[..length];

    private static string? StateForCity(string city) => NormalizeCityAlias(city) switch
    {
        "indore" or "bhopal" or "gwalior" or "jabalpur" or "ujjain" or "dewas" or "ratlam" or "dhar" or "pithampur" or "mhow" => "Madhya Pradesh",
        "mumbai" or "pune" or "nagpur" or "nashik" or "thane" => "Maharashtra",
        "delhi" or "new delhi" or "noida" or "gurugram" or "faridabad" or "ghaziabad" => "Delhi NCR",
        "bengaluru" or "mysore" => "Karnataka",
        "hyderabad" or "secunderabad" => "Telangana",
        "chennai" or "coimbatore" or "madurai" => "Tamil Nadu",
        "kolkata" or "howrah" => "West Bengal",
        "ahmedabad" or "surat" or "vadodara" or "rajkot" => "Gujarat",
        "jaipur" or "udaipur" or "jodhpur" or "kota" => "Rajasthan",
        "lucknow" or "kanpur" or "agra" or "varanasi" or "prayagraj" => "Uttar Pradesh",
        _ => null
    };

    public void Dispose()
    {
        _requestGate.Dispose();
        _http.Dispose();
    }

    private sealed record ScoredResult(GoogleResult Result, bool CityMatches, decimal Confidence);
    private sealed class GoogleEnvelope
    {
        public string? Status { get; set; }
        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
        public List<GoogleResult> Results { get; set; } = [];
    }
    private sealed class GoogleResult
    {
        [JsonPropertyName("place_id")]
        public string? PlaceId { get; set; }
        [JsonPropertyName("formatted_address")]
        public string? FormattedAddress { get; set; }
        public List<string> Types { get; set; } = [];
        [JsonPropertyName("address_components")]
        public List<GoogleAddressComponent> AddressComponents { get; set; } = [];
        public GoogleGeometry? Geometry { get; set; }
    }
    private sealed class GoogleAddressComponent
    {
        [JsonPropertyName("long_name")]
        public string? LongName { get; set; }
        [JsonPropertyName("short_name")]
        public string? ShortName { get; set; }
        public List<string> Types { get; set; } = [];
    }
    private sealed class GoogleGeometry
    {
        public GoogleLocation? Location { get; set; }
        [JsonPropertyName("location_type")]
        public string? LocationType { get; set; }
    }
    private sealed class GoogleLocation
    {
        public decimal Lat { get; set; }
        public decimal Lng { get; set; }
    }
}

public static class CityExtractor
{
    private static readonly string[] KnownCities =
    [
        "Indore", "Mumbai", "Delhi", "New Delhi", "Bangalore", "Bengaluru",
        "Hyderabad", "Chennai", "Kolkata", "Pune", "Ahmedabad", "Jaipur",
        "Lucknow", "Surat", "Kanpur", "Nagpur", "Bhopal", "Gwalior",
        "Jabalpur", "Ujjain", "Dewas", "Ratlam", "Pithampur", "Mhow",
        "Gurgaon", "Gurugram", "Noida", "Ghaziabad", "Faridabad", "Thane",
        "Nashik", "Vadodara", "Rajkot", "Chandigarh", "Ludhiana", "Amritsar",
        "Coimbatore", "Madurai", "Kochi", "Trivandrum", "Patna", "Ranchi",
        "Dehradun", "Raipur", "Bhubaneswar", "Guwahati", "Goa", "Udaipur",
        "Jodhpur", "Kota", "Agra", "Varanasi", "Mysore", "Secunderabad"
    ];

    public static string NormalizeDefaultCity(string? defaultCity)
    {
        var candidate = Regex.Replace((defaultCity ?? string.Empty).Trim(), @"\s+", " ");
        if (candidate.Length is < 2 or > 100 || !Regex.IsMatch(candidate, @"^[\p{L} .'-]+$"))
            return "Indore";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(candidate.ToLowerInvariant());
    }

    public static string ExtractCity(string? location, string defaultCity = "Indore")
    {
        var fallback = NormalizeDefaultCity(defaultCity);
        if (string.IsNullOrWhiteSpace(location)) return fallback;
        foreach (var city in KnownCities.OrderByDescending(value => value.Length))
            if (Regex.IsMatch(location, $@"\b{Regex.Escape(city)}\b", RegexOptions.IgnoreCase))
                return NormalizeDefaultCity(city);
        return fallback;
    }

    public static string RemoveCityFromLocation(string location, string city)
    {
        if (string.IsNullOrWhiteSpace(location)) return string.Empty;
        var clean = Regex.Replace(location, $@"\b{Regex.Escape(city)}\b", string.Empty, RegexOptions.IgnoreCase).Trim();
        return clean.Trim(' ', ',', '.', '-', ':');
    }
}
