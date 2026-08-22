using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using PropSeekr.Data;
using PropSeekr.DTOs.Search;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public class SearchPropertyService : ISearchPropertyService
{
    private readonly AppDbContext _dbContext;

    public SearchPropertyService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SearchPropertyResponseDto> SearchPropertiesAsync(SearchPropertyRequestDto request, Guid userId)
    {
        try
        {
            var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            var isAdmin = user != null && (string.Equals(user.Email, "admin@gmail.com", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(user.Email, "propseekr@gmail.com", StringComparison.OrdinalIgnoreCase));

            if (isAdmin)
            {
                var adminPage = request.Pagination.Page > 0 ? request.Pagination.Page : 1;
                var adminLimit = request.Pagination.Limit > 0 ? request.Pagination.Limit : 20;
                var adminSkip = (adminPage - 1) * adminLimit;

                var supplyResults = new List<PropertySearchResultItemDto>();
                var demandRequirements = new List<RequirementSearchResultItemDto>();

                var conn = _dbContext.Database.GetDbConnection();
                var wasOpen = conn.State == System.Data.ConnectionState.Open;
                if (!wasOpen) await conn.OpenAsync();

                // 1. Fetch Listings
                int totalListings = 0;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM public.listings;";
                    totalListings = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT 
                            l.listingid,
                            l.raw_message_text,
                            l.listing_type,
                            l.property_type,
                            l.configuration,
                            l.price,
                            l.furnishing,
                            l.facing,
                            l.floor_number,
                            ml.area,
                            b.name AS broker_name,
                            b.phone_number AS broker_phone
                        FROM public.listings l
                        LEFT JOIN public.master ml ON ml.masterid = l.master_id
                        LEFT JOIN public.brokers b ON b.brokerid = l.broker_id
                        ORDER BY l.last_refreshed_at DESC
                        LIMIT @limit OFFSET @offset;";

                    var pLimit = cmd.CreateParameter();
                    pLimit.ParameterName = "@limit";
                    pLimit.Value = adminLimit;
                    cmd.Parameters.Add(pLimit);

                    var pOffset = cmd.CreateParameter();
                    pOffset.ParameterName = "@offset";
                    pOffset.Value = adminSkip;
                    cmd.Parameters.Add(pOffset);

                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var id = Convert.ToInt32(reader["listingid"]);
                        var text = reader["raw_message_text"] as string ?? string.Empty;
                        var type = reader["listing_type"] as string ?? "SELL";
                        var propType = reader["property_type"] as string ?? "Flat";
                        var config = reader["configuration"] as string ?? "2BHK";
                        var priceVal = reader["price"];
                        decimal? price = (priceVal != DBNull.Value && priceVal != null) ? Convert.ToDecimal(priceVal) : null;
                        var furnishing = reader["furnishing"] as string ?? "Semi-Furnished";
                        var facing = reader["facing"] as string ?? "West Facing";
                        var floorVal = reader["floor_number"];
                        int? floor = (floorVal != DBNull.Value && floorVal != null) ? Convert.ToInt32(floorVal) : null;
                        var locality = reader["area"] as string ?? "Indore";
                        var bName = reader["broker_name"] as string ?? "PropSeekr";
                        var bPhone = reader["broker_phone"] as string ?? "N/A";

                        supplyResults.Add(MapListingToPropertySearchResultItemDto(id, text, type, propType, config, price, furnishing, facing, floor, locality, bName, bPhone));
                    }
                }

                // 2. Fetch Requirements
                int totalRequirements = 0;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM public.requirements;";
                    totalRequirements = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT 
                            r.requirementid,
                            r.raw_message_text,
                            r.requirement_type,
                            r.property_type,
                            r.configurations,
                            r.budget,
                            mr.area,
                            b.name AS broker_name
                        FROM public.requirements r
                        LEFT JOIN public.master mr ON mr.masterid = r.preferred_locality_ids[1]
                        LEFT JOIN public.brokers b ON b.brokerid = r.broker_id
                        ORDER BY r.requirementid DESC
                        LIMIT @limit OFFSET @offset;";

                    var pLimit = cmd.CreateParameter();
                    pLimit.ParameterName = "@limit";
                    pLimit.Value = adminLimit;
                    cmd.Parameters.Add(pLimit);

                    var pOffset = cmd.CreateParameter();
                    pOffset.ParameterName = "@offset";
                    pOffset.Value = adminSkip;
                    cmd.Parameters.Add(pOffset);

                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var id = Convert.ToInt32(reader["requirementid"]);
                        var text = reader["raw_message_text"] as string ?? string.Empty;
                        var type = reader["requirement_type"] as string ?? "BUY";
                        var propType = reader["property_type"] as string ?? "Flat";
                        
                        string[] configs = null;
                        if (reader["configurations"] is string[] confArr)
                        {
                            configs = confArr;
                        }
                        
                        var budgetVal = reader["budget"];
                        decimal? budget = (budgetVal != DBNull.Value && budgetVal != null) ? Convert.ToDecimal(budgetVal) : null;
                        var locality = reader["area"] as string ?? "Indore";
                        var bName = reader["broker_name"] as string ?? "PropSeekr";

                        demandRequirements.Add(MapRequirementToRequirementSearchResultItemDto(id, text, type, propType, configs, budget, locality, bName));
                    }
                }

                if (!wasOpen) await conn.CloseAsync();

                var adminIsDemandTab = string.Equals(request.ListingType, "DEMAND", StringComparison.OrdinalIgnoreCase);

                return new SearchPropertyResponseDto
                {
                    Status = "success",
                    AvailableCount = totalListings,
                    LookingCount = totalRequirements,
                    TotalCount = adminIsDemandTab ? totalRequirements : totalListings,
                    Page = adminPage,
                    Limit = adminLimit,
                    Results = supplyResults,
                    Requirements = demandRequirements
                };
            }

            var baseQuery = _dbContext.PropertyRequests.AsQueryable();
            var centre = BuildSearchCentre(request.Location);

            // Filter by transaction type
            if (!string.IsNullOrWhiteSpace(request.TransactionType))
            {
                var searchTxType = request.TransactionType.Trim().ToUpperInvariant();
                if (searchTxType == "BUY")
                {
                    baseQuery = baseQuery.Where(p => p.TransactionType == "BUY");
                }
                else if (searchTxType == "SELL" || searchTxType == "SALE")
                {
                    baseQuery = baseQuery.Where(p => p.TransactionType == "SELL");
                }
                else if (searchTxType == "BUY_SELL")
                {
                    baseQuery = baseQuery.Where(p => p.TransactionType == "BUY" || p.TransactionType == "SELL");
                }
                else
                {
                    baseQuery = baseQuery.Where(p => p.TransactionType == request.TransactionType);
                }
            }

            // Filter by location (city and locality)
            if (!string.IsNullOrWhiteSpace(request.Location.City))
            {
                baseQuery = baseQuery.Where(p => p.City.ToLower() == request.Location.City.ToLower());
            }

            if (!string.IsNullOrWhiteSpace(request.Location.Locality))
            {
                baseQuery = baseQuery.Where(p => p.Locality.ToLower() == request.Location.Locality.ToLower());
            }

            if (centre != null && request.Location.RadiusKm > 0)
            {
                var radiusMetres = request.Location.RadiusKm * 1000.0;
                baseQuery = baseQuery.Where(p =>
                    p.Location != null &&
                    p.Location.Distance(centre) <= radiusMetres);
            }

            // Filter by Category
            if (!string.IsNullOrWhiteSpace(request.Category))
            {
                baseQuery = baseQuery.Where(p => p.Category.ToLower() == request.Category.ToLower());
            }

            // Filter by budget
            if (request.Budget != null)
            {
                baseQuery = FilterByBudget(baseQuery, request.Budget);
            }

            // Filter by search query (title and user info)
            if (!string.IsNullOrWhiteSpace(request.SearchQuery))
            {
                var searchQuery = request.SearchQuery.ToLower();
                baseQuery = baseQuery.Where(p =>
                    p.Title.ToLower().Contains(searchQuery) ||
                    (p.User != null && p.User.Name.ToLower().Contains(searchQuery)));
            }

            // Apply custom filters if present
            if (request.Filters != null)
            {
                baseQuery = ApplyCustomFilters(baseQuery, request.Filters);
            }

            // Separate supply (results) and demand (requirements) queries
            var supplyQuery = baseQuery.Where(p =>
                p.ListingType.ToLower() == "supply" ||
                (string.IsNullOrWhiteSpace(p.ListingType) && p.Status.ToLower() == "active"));

            var demandQuery = baseQuery.Where(p =>
                p.ListingType.ToLower() == "demand" ||
                (string.IsNullOrWhiteSpace(p.ListingType) && p.Status.ToLower() == "looking"));

            var availableCount = await supplyQuery.CountAsync();
            var lookingCount = await demandQuery.CountAsync();

            // Calculate pagination parameters
            var pageNumber = request.Pagination.Page > 0 ? request.Pagination.Page : 1;
            var limit = request.Pagination.Limit > 0 ? request.Pagination.Limit : 20;
            var skip = (pageNumber - 1) * limit;

            List<Models.PropertyRequest> supplyRequests = new();
            List<Models.PropertyRequest> demandRequests = new();

            var isDemandTab = string.Equals(request.ListingType, "DEMAND", StringComparison.OrdinalIgnoreCase);

            if (isDemandTab)
            {
                // Paginate demand requirements
                demandRequests = await demandQuery
                    .Include(p => p.User)
                    .OrderByDescending(p => p.PostedAt)
                    .Skip(skip)
                    .Take(limit)
                    .ToListAsync();

                // Get first page of supply results as default companion list
                supplyRequests = await supplyQuery
                    .Include(p => p.User)
                    .OrderByDescending(p => p.PostedAt)
                    .Take(5)
                    .ToListAsync();
            }
            else
            {
                // Paginate supply results
                supplyRequests = await supplyQuery
                    .Include(p => p.User)
                    .OrderByDescending(p => p.PostedAt)
                    .Skip(skip)
                    .Take(limit)
                    .ToListAsync();

                // Get first page of demand requirements as default companion list
                demandRequests = await demandQuery
                    .Include(p => p.User)
                    .OrderByDescending(p => p.PostedAt)
                    .Take(5)
                    .ToListAsync();
            }

            var resultsMapped = supplyRequests.Select(pr => MapToPropertySearchResultItemDto(pr, centre)).ToList();
            var requirementsMapped = demandRequests.Select(pr => MapToRequirementSearchResultItemDto(pr)).ToList();

            return new SearchPropertyResponseDto
            {
                Status = "success",
                AvailableCount = availableCount,
                LookingCount = lookingCount,
                TotalCount = isDemandTab ? lookingCount : availableCount,
                Page = pageNumber,
                Limit = limit,
                Results = resultsMapped,
                Requirements = requirementsMapped
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Error searching property requests: {ex.Message}", ex);
        }
    }

    private static Point? BuildSearchCentre(LocationDto location)
    {
        if (location == null)
            return null;

        if (location.Lat == 0 && location.Lng == 0 && location.RadiusKm <= 0)
            return null;

        return new Point(location.Lng, location.Lat) { SRID = 4326 };
    }

    private IQueryable<Models.PropertyRequest> FilterByBudget(
        IQueryable<Models.PropertyRequest> query,
        BudgetFilterDto budget)
    {
        if (budget.Min.HasValue)
        {
            query = query.Where(p => p.BudgetMax >= budget.Min.Value);
        }

        if (budget.Max.HasValue)
        {
            query = query.Where(p => p.BudgetMin <= budget.Max.Value);
        }

        return query;
    }

    private IQueryable<Models.PropertyRequest> ApplyCustomFilters(
        IQueryable<Models.PropertyRequest> query,
        FiltersDto filters)
    {
        if (filters.PropertyTypes != null && filters.PropertyTypes.Any())
        {
            var propertyTypesLower = filters.PropertyTypes.Select(pt => pt.ToLower()).ToList();
            query = query.Where(p => propertyTypesLower.Any(pt => p.PropertyTypesJson.ToLower().Contains(pt)));
        }

        return query;
    }

    private PropertySearchResultItemDto MapToPropertySearchResultItemDto(
        Models.PropertyRequest pr,
        Point? centre = null)
    {
        var title = pr.Title ?? string.Empty;
        
        // 1. Parse BHK
        var bhk = "2BHK";
        if (title.Contains("1BHK", StringComparison.OrdinalIgnoreCase) || title.Contains("1 bhk", StringComparison.OrdinalIgnoreCase)) bhk = "1BHK";
        else if (title.Contains("3BHK", StringComparison.OrdinalIgnoreCase) || title.Contains("3 bhk", StringComparison.OrdinalIgnoreCase)) bhk = "3BHK";
        else if (title.Contains("4BHK", StringComparison.OrdinalIgnoreCase) || title.Contains("4 bhk", StringComparison.OrdinalIgnoreCase)) bhk = "4BHK";

        // 2. Parse PropertyType
        var propertyType = "Flat";
        if (title.Contains("Penthouse", StringComparison.OrdinalIgnoreCase)) propertyType = "Penthouse";
        else if (title.Contains("Villa", StringComparison.OrdinalIgnoreCase)) propertyType = "Villa";
        else if (title.Contains("Office", StringComparison.OrdinalIgnoreCase)) propertyType = "Office Space";
        else if (title.Contains("Shop", StringComparison.OrdinalIgnoreCase)) propertyType = "Shop";

        // 3. Parse Furnishing
        var furnishing = "Semi-Furnished";
        if (title.Contains("fully furnished", StringComparison.OrdinalIgnoreCase) || title.Contains("fully-furnished", StringComparison.OrdinalIgnoreCase)) furnishing = "Fully Furnished";
        else if (title.Contains("unfurnished", StringComparison.OrdinalIgnoreCase)) furnishing = "Unfurnished";

        // 4. Parse Floor
        var floor = "2nd Floor";
        if (title.Contains("1st floor", StringComparison.OrdinalIgnoreCase) || title.Contains("first floor", StringComparison.OrdinalIgnoreCase)) floor = "1st Floor";
        else if (title.Contains("3rd floor", StringComparison.OrdinalIgnoreCase) || title.Contains("third floor", StringComparison.OrdinalIgnoreCase)) floor = "3rd Floor";
        else if (title.Contains("4th floor", StringComparison.OrdinalIgnoreCase) || title.Contains("fourth floor", StringComparison.OrdinalIgnoreCase)) floor = "4th Floor";
        else if (title.Contains("ground floor", StringComparison.OrdinalIgnoreCase)) floor = "Ground Floor";

        // 5. Parse Facing
        var facing = "West Facing";
        if (title.Contains("east facing", StringComparison.OrdinalIgnoreCase) || title.Contains("east-facing", StringComparison.OrdinalIgnoreCase)) facing = "East Facing";
        else if (title.Contains("north facing", StringComparison.OrdinalIgnoreCase) || title.Contains("north-facing", StringComparison.OrdinalIgnoreCase)) facing = "North Facing";
        else if (title.Contains("south facing", StringComparison.OrdinalIgnoreCase) || title.Contains("south-facing", StringComparison.OrdinalIgnoreCase)) facing = "South Facing";

        // 6. Subtitle
        var subtitle = $"{pr.Locality}, {pr.City} · {floor} · {facing}";

        // 7. Area Size
        long areaSize = 950;
        try
        {
            if (!string.IsNullOrWhiteSpace(pr.RequiredAreaJson))
            {
                using var areaDoc = JsonDocument.Parse(pr.RequiredAreaJson);
                var areaRoot = areaDoc.RootElement;
                if (areaRoot.TryGetProperty("min", out var minProp))
                {
                    areaSize = Convert.ToInt64(minProp.GetDouble());
                }
            }
        }
        catch {}

        // 8. Distance
        var distance = centre != null && pr.Location != null
            ? GetDistanceKm(pr.Location, centre)
            : 0.0;
        var formattedDistance = distance > 0 ? $"{distance:0.0} km" : "1.2 km";
        var isNearby = distance > 0 ? distance <= 2.0 : true;
        var locationLabel = $"{formattedDistance} · {pr.Locality} main road";

        // 9. Features
        var features = new List<FeatureItemDto>
        {
            new() { Icon = furnishing == "Fully Furnished" ? "🛋️" : "🪑", Label = furnishing },
            new() { Icon = "🚗", Label = "Reserved Parking" },
            new() { Icon = "🏢", Label = floor },
            new() { Icon = "🧭", Label = facing },
            new() { Icon = "🛁", Label = bhk == "3BHK" ? "3 Bathrooms" : "2 Bathrooms" },
            new() { Icon = "⚡", Label = "24/7 Power Backup" }
        };

        // 10. Preferences
        var preferences = new List<PreferenceItemDto>();
        if (string.Equals(pr.Category, "Residential", StringComparison.OrdinalIgnoreCase))
        {
            preferences.Add(new() { Label = "Family preferred", Allowed = true });
            preferences.Add(new() { Label = "Working professionals", Allowed = true });
            preferences.Add(new() { Label = "No pets allowed", Allowed = false });
            preferences.Add(new() { Label = "No bachelors", Allowed = false });
        }
        else
        {
            preferences.Add(new() { Label = "Any tenant welcome", Allowed = true });
        }

        // 11. Broker info
        var brokerName = pr.User != null ? pr.User.Name : "Rahul Kumar";
        var initials = pr.User != null ? GetInitials(pr.User.Name) : "RK";
        var brokerSub = $"{pr.Locality} · {(pr.User != null && (pr.User.Email == "admin@gmail.com" || pr.User.Email == "propseekr@gmail.com") ? "PropSeekr" : "Network")}";

        return new PropertySearchResultItemDto
        {
            Id = pr.Id.ToString(),
            Title = pr.Title ?? string.Empty,
            Subtitle = subtitle,
            Category = pr.Category,
            PropertyType = propertyType,
            Bhk = bhk,
            Status = pr.Status,
            Price = pr.BudgetMin ?? 0,
            BuiltUpSize = areaSize,
            AvailableFrom = "Immediate",
            CreatedAt = pr.PostedAt,
            UnlockCost = pr.TransactionType == "SELL" ? 2 : 1,
            IsNearby = isNearby,
            LocationLabel = locationLabel,
            BrokerName = brokerName,
            BrokerInitials = initials,
            BrokerSub = brokerSub,
            Features = features,
            Preferences = preferences
        };
    }

    private RequirementSearchResultItemDto MapToRequirementSearchResultItemDto(Models.PropertyRequest pr)
    {
        var min = pr.BudgetMin ?? 0;
        var max = pr.BudgetMax ?? 0;
        string budgetStr;
        if (max >= 10000000)
        {
            budgetStr = $"Budget ₹{min/10000000.0:0.##}Cr–{max/10000000.0:0.##}Cr";
        }
        else if (max >= 100000)
        {
            budgetStr = $"Budget ₹{min/100000.0:0.##}L–{max/100000.0:0.##}L";
        }
        else if (max >= 1000)
        {
            budgetStr = $"Budget ₹{min/1000.0:0.##}K–{max/1000.0:0.##}K";
        }
        else
        {
            budgetStr = $"Budget ₹{min}–{max}";
        }

        var brokerName = pr.User != null ? pr.User.Name : "Rahul Kumar";
        var initials = pr.User != null ? GetInitials(pr.User.Name) : "RK";

        return new RequirementSearchResultItemDto
        {
            Id = pr.Id.ToString(),
            Title = pr.Title ?? string.Empty,
            Sub = $"{budgetStr} · {pr.Locality}",
            Initials = initials,
            Color = GetDeterministicColor(brokerName)
        };
    }

    private static readonly string[] PremiumColors = new[] { "#0A6E5E", "#0A5E6E", "#6E0A5E", "#6E5E0A", "#0A3C6E", "#5E6E0A", "#E53E3E", "#3182CE", "#319795", "#805AD5" };
    private static string GetDeterministicColor(string name)
    {
        int hash = 0;
        foreach (char c in name) hash += c;
        return PremiumColors[Math.Abs(hash) % PremiumColors.Length];
    }

    private static string GetInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "PS";

        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0][..1].ToUpper();

        return (parts[0][..1] + parts[^1][..1]).ToUpper();
    }

    private static double GetDistanceKm(Point location, Point centre)
    {
        var lat1 = ToRadians(location.Y);
        var lat2 = ToRadians(centre.Y);
        var dLat = ToRadians(centre.Y - location.Y);
        var dLng = ToRadians(centre.X - location.X);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return 6371.0 * c;
    }

    private static double ToRadians(double value) => value * Math.PI / 180.0;

    private PropertySearchResultItemDto MapListingToPropertySearchResultItemDto(
        int id, string text, string type, string propType, string config, decimal? price, string furnishing, string facing, int? floor, string locality, string brokerName, string brokerPhone)
    {
        var bhk = config ?? "2BHK";
        var propertyType = propType ?? "Flat";
        var furnishingStatus = furnishing ?? "Semi-Furnished";
        var floorStatus = floor.HasValue ? $"{floor.Value}th Floor" : "Ground Floor";
        var facingStatus = facing ?? "West Facing";

        var subtitle = $"{locality ?? "Indore"} · {floorStatus} · {facingStatus}";

        var features = new List<FeatureItemDto>
        {
            new() { Icon = furnishingStatus == "Fully Furnished" ? "🛋️" : "🪑", Label = furnishingStatus },
            new() { Icon = "🚗", Label = "Reserved Parking" },
            new() { Icon = "🏢", Label = floorStatus },
            new() { Icon = "🧭", Label = facingStatus },
            new() { Icon = "🛁", Label = "2 Bathrooms" },
            new() { Icon = "⚡", Label = "24/7 Power Backup" }
        };

        var preferences = new List<PreferenceItemDto>
        {
            new() { Label = "Family preferred", Allowed = true },
            new() { Label = "Working professionals", Allowed = true }
        };

        var initials = string.IsNullOrWhiteSpace(brokerName) ? "PS" : (brokerName.Split(' ').Length > 1 ? (brokerName.Split(' ')[0][..1] + brokerName.Split(' ')[^1][..1]).ToUpper() : brokerName[..1].ToUpper());

        return new PropertySearchResultItemDto
        {
            Id = id.ToString(),
            Title = string.IsNullOrWhiteSpace(text) ? $"{bhk} {propertyType} in {locality}" : text,
            Subtitle = subtitle,
            Category = (string.Equals(propertyType, "plot", StringComparison.OrdinalIgnoreCase) || string.Equals(propertyType, "land", StringComparison.OrdinalIgnoreCase)) ? "Plot/Land" : "Residential",
            PropertyType = propertyType,
            Bhk = bhk,
            Status = "ACTIVE",
            Price = price.HasValue ? (long)price.Value : 0,
            BuiltUpSize = 1000,
            AvailableFrom = "Immediate",
            CreatedAt = DateTime.UtcNow,
            UnlockCost = 1,
            IsNearby = true,
            LocationLabel = $"1.2 km · {locality} main road",
            BrokerName = brokerName ?? "PropSeekr",
            BrokerInitials = initials,
            BrokerSub = $"{locality} · PropSeekr",
            Features = features,
            Preferences = preferences
        };
    }

    private RequirementSearchResultItemDto MapRequirementToRequirementSearchResultItemDto(
        int id, string text, string type, string propType, string[]? configs, decimal? budget, string locality, string brokerName)
    {
        var budgetStr = budget.HasValue ? $"Budget ₹{budget.Value:N0}" : "Budget Contact Owner";
        if (budget.HasValue)
        {
            var bVal = budget.Value;
            if (bVal >= 10000000M) budgetStr = $"Budget ₹{bVal/10000000M:0.##}Cr";
            else if (bVal >= 100000M) budgetStr = $"Budget ₹{bVal/100000M:0.##}L";
            else if (bVal >= 1000M) budgetStr = $"Budget ₹{bVal/1000M:0.##}K";
        }

        var initials = string.IsNullOrWhiteSpace(brokerName) ? "PS" : (brokerName.Split(' ').Length > 1 ? (brokerName.Split(' ')[0][..1] + brokerName.Split(' ')[^1][..1]).ToUpper() : brokerName[..1].ToUpper());

        return new RequirementSearchResultItemDto
        {
            Id = id.ToString(),
            Title = string.IsNullOrWhiteSpace(text) ? $"Requirement in {locality}" : text,
            Sub = $"{budgetStr} · {locality}",
            Initials = initials,
            Color = "#0A6E5E"
        };
    }
}
