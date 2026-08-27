using System.Text.Json;
using System.Web;

namespace propseekr_file_processor
{
    /// <summary>
    /// Geocodes location strings using OpenStreetMap Nominatim (free, no API key).
    /// Returns lat/lng coordinates for master table population.
    /// </summary>
    public class GeocodingService : IDisposable
    {
        private readonly HttpClient _http;
        private DateTime _lastRequestTime = DateTime.MinValue;

        // In-memory cache to avoid re-geocoding same location within a batch
        private readonly Dictionary<string, (decimal lat, decimal lng)?> _cache
            = new(StringComparer.OrdinalIgnoreCase);

        // Cache for city-center coordinates to identify fallback responses
        private readonly Dictionary<string, (decimal lat, decimal lng)?> _cityCenterCache
            = new(StringComparer.OrdinalIgnoreCase);

        public GeocodingService()
        {
            _http = new HttpClient();
            // Nominatim requires a valid User-Agent identifying your app
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "PropSeekr/1.0 (property-matching-platform)");
        }

        /// <summary>
        /// Geocode a location string (e.g. "Super Corridor, Indore, India")
        /// Returns (lat, lng) or null if not found.
        /// </summary>
        public async Task<(decimal lat, decimal lng)?> GeocodeAsync(string area, string city)
        {
            var cacheKey = $"{area}|{city}";

            // Check cache first
            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;

            try
            {
                // Respect Nominatim rate limit: max 1 request per second
                await RespectRateLimitAsync();

                // Build search query: "Super Corridor, Indore, Madhya Pradesh, India"
                var query = BuildSearchQuery(area, city);
                var encodedQuery = HttpUtility.UrlEncode(query);

                var url = $"https://nominatim.openstreetmap.org/search" +
                          $"?q={encodedQuery}" +
                          $"&format=json" +
                          $"&limit=1" +
                          $"&countrycodes=in"; // Restrict to India

                var response = await _http.GetStringAsync(url);
                var results = JsonSerializer.Deserialize<List<NominatimResult>>(response,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (results != null && results.Count > 0)
                {
                    var first = results[0];
                    if (decimal.TryParse(first.Lat, out var lat) &&
                        decimal.TryParse(first.Lon, out var lng))
                    {
                        // Check if these coordinates are Indore's or current city's default center coordinates
                        if (!string.IsNullOrWhiteSpace(area) && !area.Equals(city, StringComparison.OrdinalIgnoreCase))
                        {
                            var cityCoords = await GetCityCenterCoordsAsync(city);
                            if (cityCoords.HasValue)
                            {
                                // If they are exactly the city center coordinates (within ~11 meters / 0.0001 deg),
                                // it means Nominatim fell back to city-level geocoding. Reject it for specific sub-areas.
                                if (Math.Abs(lat - cityCoords.Value.lat) < 0.0001m &&
                                    Math.Abs(lng - cityCoords.Value.lng) < 0.0001m)
                                {
                                    Console.WriteLine($"Geocoding for '{area}, {city}' returned generic city-center coordinates. Rejecting as fallback.");
                                    _cache[cacheKey] = null;
                                    return null;
                                }
                            }
                        }

                        var coords = (lat, lng);
                        _cache[cacheKey] = coords;
                        return coords;
                    }
                }

                /* PREVIOUS CODE / BACKUP: City fallback removed to prevent generic coordinates.
                if (!string.IsNullOrWhiteSpace(area))
                {
                    await RespectRateLimitAsync();

                    var fallbackQuery = HttpUtility.UrlEncode($"{city}, India");
                    var fallbackUrl = $"https://nominatim.openstreetmap.org/search" +
                                     $"?q={fallbackQuery}" +
                                     $"&format=json" +
                                     $"&limit=1" +
                                     $"&countrycodes=in";

                    var fallbackResponse = await _http.GetStringAsync(fallbackUrl);
                    var fallbackResults = JsonSerializer.Deserialize<List<NominatimResult>>(
                        fallbackResponse,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (fallbackResults != null && fallbackResults.Count > 0)
                    {
                        var first = fallbackResults[0];
                        if (decimal.TryParse(first.Lat, out var lat2) &&
                            decimal.TryParse(first.Lon, out var lng2))
                        {
                            // Use city-level coords as approximate
                            var coords = (lat2, lng2);
                            _cache[cacheKey] = coords;
                            return coords;
                        }
                    }
                }
                */

                // Not found
                _cache[cacheKey] = null;
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Geocoding failed for '{area}, {city}': {ex.Message}");
                _cache[cacheKey] = null;
                return null;
            }
        }

        /// <summary>
        /// Batch geocode all master rows that have NULL lat/lng.
        /// Call this after ingestion to fill missing coordinates.
        /// </summary>
        public async Task<int> BackfillMasterCoordinatesAsync(Npgsql.NpgsqlConnection conn)
        {
            // Fetch all master rows with NULL coordinates
            var rows = new List<(int id, string area, string city)>();

            await using (var cmd = new Npgsql.NpgsqlCommand(@"
                SELECT masterid, area, city FROM master
                WHERE lat IS NULL OR lng IS NULL
                ORDER BY masterid", conn))
            {
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rows.Add((
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetString(2)
                    ));
                }
            }

            if (rows.Count == 0) return 0;

            int updated = 0;

            foreach (var (id, area, city) in rows)
            {
                var coords = await GeocodeAsync(area, city);
                if (coords.HasValue)
                {
                    await using var updateCmd = new Npgsql.NpgsqlCommand(@"
                        UPDATE master SET lat = @lat, lng = @lng
                        WHERE masterid = @id", conn);
                    updateCmd.Parameters.AddWithValue("id", id);
                    updateCmd.Parameters.AddWithValue("lat", coords.Value.lat);
                    updateCmd.Parameters.AddWithValue("lng", coords.Value.lng);
                    await updateCmd.ExecuteNonQueryAsync();
                    updated++;

                    Console.WriteLine(
                        $"Geocoded: {area}, {city} â†’ ({coords.Value.lat}, {coords.Value.lng})");
                }
                else
                {
                    Console.WriteLine($"Geocoding failed: {area}, {city} â†’ no results");
                }
            }

            return updated;
        }

        private async Task<(decimal lat, decimal lng)?> GetCityCenterCoordsAsync(string city)
        {
            if (string.IsNullOrWhiteSpace(city)) return null;
            if (_cityCenterCache.TryGetValue(city, out var coords)) return coords;

            try
            {
                await RespectRateLimitAsync();
                var encodedQuery = HttpUtility.UrlEncode($"{city}, India");
                var url = $"https://nominatim.openstreetmap.org/search" +
                          $"?q={encodedQuery}" +
                          $"&format=json" +
                          $"&limit=1" +
                          $"&countrycodes=in";

                var response = await _http.GetStringAsync(url);
                var results = JsonSerializer.Deserialize<List<NominatimResult>>(response,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (results != null && results.Count > 0)
                {
                    var first = results[0];
                    if (decimal.TryParse(first.Lat, out var lat) &&
                        decimal.TryParse(first.Lon, out var lng))
                    {
                        var cityCoords = (lat, lng);
                        _cityCenterCache[city] = cityCoords;
                        return cityCoords;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get city center coords for {city}: {ex.Message}");
            }

            _cityCenterCache[city] = null;
            return null;
        }

        // â”€â”€ HELPERS â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private string BuildSearchQuery(string area, string city)
        {
            // Build query with increasing specificity
            // "Super Corridor, Indore, Madhya Pradesh, India"
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(area))
                parts.Add(area.Trim());

            if (!string.IsNullOrWhiteSpace(city))
                parts.Add(city.Trim());

            // Add state hint based on city (helps Nominatim accuracy)
            var state = GetStateForCity(city);
            if (!string.IsNullOrWhiteSpace(state))
                parts.Add(state);

            parts.Add("India");

            return string.Join(", ", parts);
        }

        private static string? GetStateForCity(string? city)
        {
            if (string.IsNullOrWhiteSpace(city)) return null;

            // Common city â†’ state mapping for India
            return city.Trim().ToLower() switch
            {
                "indore" or "bhopal" or "gwalior" or "jabalpur" or "ujjain" or
                "dewas" or "ratlam" or "dhar" or "pithampur" or "mhow"
                    => "Madhya Pradesh",

                "mumbai" or "pune" or "nagpur" or "nashik" or "thane"
                    => "Maharashtra",

                "delhi" or "new delhi" or "noida" or "gurgaon" or "gurugram" or
                "faridabad" or "ghaziabad"
                    => "Delhi NCR",

                "bangalore" or "bengaluru" or "mysore"
                    => "Karnataka",

                "hyderabad" or "secunderabad"
                    => "Telangana",

                "chennai" or "coimbatore" or "madurai"
                    => "Tamil Nadu",

                "kolkata" or "howrah"
                    => "West Bengal",

                "ahmedabad" or "surat" or "vadodara" or "rajkot"
                    => "Gujarat",

                "jaipur" or "udaipur" or "jodhpur" or "kota"
                    => "Rajasthan",

                "lucknow" or "kanpur" or "agra" or "varanasi" or "prayagraj"
                    => "Uttar Pradesh",

                "chandigarh" or "mohali" or "ludhiana" or "amritsar"
                    => "Punjab",

                "patna" or "gaya"
                    => "Bihar",

                "bhubaneswar" or "cuttack"
                    => "Odisha",

                "kochi" or "trivandrum" or "thiruvananthapuram" or "calicut"
                    => "Kerala",

                "raipur" or "bilaspur"
                    => "Chhattisgarh",

                "ranchi" or "jamshedpur" or "dhanbad"
                    => "Jharkhand",

                "dehradun" or "haridwar" or "rishikesh"
                    => "Uttarakhand",

                "goa" or "panaji"
                    => "Goa",

                "guwahati" or "shillong"
                    => "Assam",

                _ => null
            };
        }

        private async Task RespectRateLimitAsync()
        {
            // Nominatim requires max 1 request per second
            var elapsed = DateTime.UtcNow - _lastRequestTime;
            if (elapsed.TotalMilliseconds < 1100)
            {
                await Task.Delay(1100 - (int)elapsed.TotalMilliseconds);
            }
            _lastRequestTime = DateTime.UtcNow;
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        // â”€â”€ NOMINATIM RESPONSE MODEL â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private class NominatimResult
        {
            public string? Lat { get; set; }
            public string? Lon { get; set; }
            public string? DisplayName { get; set; }
            public string? Type { get; set; }
        }
    }

    /// <summary>
    /// Helper to extract city from a location string.
    /// Used when city is not explicitly provided.
    /// </summary>
    public static class CityExtractor
    {
        // Known Indian cities for extraction from location text
        private static readonly string[] KnownCities = new[]
        {
            "Indore", "Mumbai", "Delhi", "Bangalore", "Bengaluru",
            "Hyderabad", "Chennai", "Kolkata", "Pune", "Ahmedabad",
            "Jaipur", "Lucknow", "Surat", "Kanpur", "Nagpur",
            "Bhopal", "Gwalior", "Jabalpur", "Ujjain", "Dewas",
            "Ratlam", "Pithampur", "Mhow", "Gurgaon", "Gurugram",
            "Noida", "Ghaziabad", "Faridabad", "Thane", "Nashik",
            "Vadodara", "Rajkot", "Chandigarh", "Ludhiana", "Amritsar",
            "Coimbatore", "Madurai", "Kochi", "Trivandrum",
            "Patna", "Ranchi", "Dehradun", "Raipur", "Bhubaneswar",
            "Guwahati", "Goa", "Udaipur", "Jodhpur", "Kota",
            "Agra", "Varanasi", "Mysore", "Secunderabad"
        };

        /// <summary>
        /// Extract city name from location text.
        /// "Super Corridor Indore" â†’ "Indore"
        /// "Vijay Nagar, Mumbai" â†’ "Mumbai"
        /// Returns defaultCity if no city found.
        /// </summary>
        public static string ExtractCity(string? location, string defaultCity = "Indore")
        {
            if (string.IsNullOrWhiteSpace(location))
                return defaultCity;

            foreach (var city in KnownCities)
            {
                if (location.Contains(city, StringComparison.OrdinalIgnoreCase))
                    return city;
            }

            return defaultCity;
        }

        /// <summary>
        /// Remove city name from location to get just the area/locality.
        /// "Super Corridor Indore" â†’ "Super Corridor"
        /// </summary>
        public static string RemoveCityFromLocation(string location, string city)
        {
            if (string.IsNullOrWhiteSpace(location)) return "";

            var clean = System.Text.RegularExpressions.Regex.Replace(
                location, $@"\b{city}\b", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

            return clean.Trim(' ', ',', '.', '-', ':');
        }
    }
}

