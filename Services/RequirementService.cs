using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using PropSeekr.Data;
using PropSeekr.DTOs.Requirements;
using PropSeekr.DTOs.Search;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public class RequirementService : IRequirementService
{
    private readonly AppDbContext _dbContext;

    public RequirementService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<MyRequirementsResponseDto> GetMyRequirementsAsync(Guid userId, PaginationDto pagination)
    {
        var pageNumber = pagination.Page > 0 ? pagination.Page : 1;
        var limit = pagination.Limit > 0 ? pagination.Limit : 20;
        var skip = (pageNumber - 1) * limit;

        var query = _dbContext.PropertyRequests
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.ListingType == "DEMAND");

        var totalCount = await query.CountAsync();

        var requirements = await query
            .OrderByDescending(p => p.PostedAt)
            .Skip(skip)
            .Take(limit)
            .ToListAsync();

        var responseItems = requirements.Select(p => {
            var requiredArea = p.RequiredAreaJson != null ? DeserializeJson<RequiredAreaDto>(p.RequiredAreaJson) : null;
            return new RequirementListItemDto
            {
                RequirementId = p.Id.ToString(),
                Description = p.Title,
                TransactionType = p.TransactionType,
                Category = p.Category,
                Budget = new BudgetResponseDto
                {
                    Min = p.BudgetMin ?? 0,
                    Max = p.BudgetMax ?? 0,
                    DisplayValue = p.BudgetMax.HasValue ? $"₹{p.BudgetMin ?? 0} - ₹{p.BudgetMax.Value}" : "",
                    Currency = "INR"
                },
                PreferredLocation = new LocationDto
                {
                    City = p.City,
                    Locality = p.Locality,
                    Lat = p.Location?.Y ?? 0,
                    Lng = p.Location?.X ?? 0,
                    RadiusKm = p.RadiusKm
                },
                RequiredArea = new RequiredAreaDto
                {
                    Min = requiredArea?.Min ?? 0,
                    Max = requiredArea?.Max ?? 0,
                    DisplayValue = requiredArea?.DisplayValue ?? string.Empty,
                    Unit = requiredArea?.Unit ?? "SQFT"
                },
                PostedAt = p.PostedAt,
                Status = p.Status
            };
        }).ToList();

        return new MyRequirementsResponseDto
        {
            Success = true,
            Metadata = new MetadataDto
            {
                TotalCount = totalCount,
                Page = pageNumber,
                Limit = limit
            },
            Data = responseItems
        };
    }

    public async Task<CreateRequirementResponseDto> AddRequirementAsync(Guid userId, CreateRequirementRequestDto request)
    {
        if (request.BudgetMax <= 0)
            throw new ArgumentException("Budget must be greater than zero.");

        if (request.MinimumSize <= 0)
            throw new ArgumentException("Minimum size must be greater than zero.");

        if (string.IsNullOrWhiteSpace(request.City) || string.IsNullOrWhiteSpace(request.Locality))
            throw new ArgumentException("City and locality are required.");

        if (request.Lat == 0 || request.Lng == 0)
            throw new ArgumentException("Valid GPS coordinates are required.");

        if (request.RadiusKm <= 0)
            throw new ArgumentException("Search radius must be greater than zero.");

        var normalizedTransactionType = string.IsNullOrWhiteSpace(request.TransactionType) ? string.Empty : request.TransactionType.Trim().ToUpperInvariant();
        if (normalizedTransactionType == "BUY_SELL" || normalizedTransactionType == "BUY" || normalizedTransactionType == "PURCHASE")
        {
            normalizedTransactionType = "BUY";
        }
        else if (normalizedTransactionType != "RENTAL")
        {
            throw new ArgumentException("Transaction type must be BUY, BUY_SELL, or RENTAL.");
        }

        var propertyRequest = new PropertyRequest
        {
            UserId = userId,
            ListingType = "DEMAND",
            TransactionType = normalizedTransactionType,
            Category = request.Category,
            Title = request.Description,
            Status = "LOOKING",
            BudgetMin = 0,
            BudgetMax = request.BudgetMax,
            City = request.City,
            Locality = request.Locality,
            Location = new Point(request.Lng, request.Lat) { SRID = 4326 },
            RadiusKm = request.RadiusKm,
            PostedAt = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow,
            RequiredAreaJson = JsonSerializer.Serialize(new RequiredAreaDto
            {
                Min = request.MinimumSize,
                Max = request.MinimumSize,
                DisplayValue = $"{request.MinimumSize} SQFT",
                Unit = "SQFT"
            }),
            BudgetJson = JsonSerializer.Serialize(new BudgetResponseDto
            {
                Min = 0,
                Max = request.BudgetMax,
                DisplayValue = $"₹{request.BudgetMax}",
                Currency = "INR"
            })
        };

        _dbContext.PropertyRequests.Add(propertyRequest);
        await _dbContext.SaveChangesAsync();

        return new CreateRequirementResponseDto
        {
            Success = true,
            RequirementId = propertyRequest.Id.ToString(),
            Message = "Requirement successfully posted."
        };
    }

    private static T? DeserializeJson<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    public async Task<MyRequirementsListResponseDto> GetMyRequirementsWithMetricsAsync(Guid userId, string? status, int page, int limit)
    {
        if (page < 1) page = 1;
        if (limit < 1 || limit > 100) limit = 20;

        var query = _dbContext.PropertyRequests
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.ListingType == "DEMAND");

        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(p => p.Status.ToLower() == status.ToLower());
        }

        var totalCount = await query.CountAsync();
        var activeCount = await _dbContext.PropertyRequests
            .AsNoTracking()
            .CountAsync(p => p.UserId == userId && p.ListingType == "DEMAND" && p.Status.ToLower() == "looking");

        var items = await query
            .OrderByDescending(p => p.PostedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var data = new List<MyRequirementItemDto>();
        foreach (var p in items)
        {
            var matchesCount = await _dbContext.PropertyRequests
                .AsNoTracking()
                .CountAsync(other => other.UserId != userId && 
                                     other.ListingType == "SUPPLY" && 
                                     other.Category == p.Category && 
                                     other.TransactionType == p.TransactionType);

            // Default fallback values
            string name = string.Empty;
            string contactNumber = string.Empty;
            string configuration = string.Empty;
            string furnishingPreference = string.Empty;
            string preferredPreference = string.Empty;
            string facing = string.Empty;
            int requiredArea = 0;
            string budgetStr = p.BudgetMax.HasValue ? $"₹{p.BudgetMin ?? 0} – {p.BudgetMax.Value}" : string.Empty;
            string projectSocietyName = string.Empty;
            string additionalNotes = string.Empty;

            try
            {
                if (!string.IsNullOrEmpty(p.FiltersJson))
                {
                    using var doc = JsonDocument.Parse(p.FiltersJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("name", out var nameProp)) name = nameProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("contactNumber", out var phoneProp)) contactNumber = phoneProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("configuration", out var configProp)) configuration = configProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("furnishingPreference", out var furnProp)) furnishingPreference = furnProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("preferredPreference", out var prefProp)) preferredPreference = prefProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("facing", out var facingProp)) facing = facingProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("requiredArea", out var areaProp)) requiredArea = areaProp.GetInt32();
                    if (root.TryGetProperty("budget", out var budgetProp)) budgetStr = budgetProp.GetString() ?? budgetStr;
                    if (root.TryGetProperty("projectSocietyName", out var projProp)) projectSocietyName = projProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("additionalNotes", out var notesProp)) additionalNotes = notesProp.GetString() ?? string.Empty;
                }
            }
            catch {}

            data.Add(new MyRequirementItemDto
            {
                Id = p.Id.ToString(),
                UserId = p.UserId,
                Name = name,
                ContactNumber = contactNumber,
                PropertyType = p.Category,
                PreferredLocation = p.Locality,
                Configuration = configuration,
                FurnishingPreference = furnishingPreference,
                PreferredPreference = preferredPreference,
                Facing = facing,
                RequiredArea = requiredArea,
                Budget = budgetStr,
                ProjectSocietyName = projectSocietyName,
                AdditionalNotes = additionalNotes,
                Latitude = p.Location?.Y ?? 0,
                Longitude = p.Location?.X ?? 0,
                RadiusKm = p.RadiusKm,
                Status = p.Status == "LOOKING" ? "Active" : p.Status,
                MatchesFound = matchesCount,
                CreatedAt = p.PostedAt,
                UpdatedAt = p.ModifiedDate
            });
        }

        return new MyRequirementsListResponseDto
        {
            Success = true,
            Page = page,
            Limit = limit,
            TotalCount = totalCount,
            ActiveCount = activeCount,
            Data = data
        };
    }

    public async Task<AddRequirementResponseDto> AddRequirementAsync(AddRequirementRequestDto request)
    {
        var normalizedTransactionType = request.ListingType.Trim().ToUpperInvariant();
        if (normalizedTransactionType == "BUY_SELL" || normalizedTransactionType == "SALE" || normalizedTransactionType == "SELL")
        {
            normalizedTransactionType = "SELL";
        }
        else
        {
            normalizedTransactionType = "RENTAL";
        }

        var (minBudget, maxBudget) = ParseBudget(request.Budget);

        var filters = new Dictionary<string, object>
        {
            { "name", request.Name },
            { "contactNumber", request.ContactNumber },
            { "configuration", request.Configuration },
            { "furnishingPreference", request.FurnishingPreference },
            { "preferredPreference", request.PreferredPreference },
            { "facing", request.Facing },
            { "requiredArea", request.RequiredArea },
            { "budget", request.Budget },
            { "projectSocietyName", request.ProjectSocietyName },
            { "additionalNotes", request.AdditionalNotes }
        };

        var filtersJson = JsonSerializer.Serialize(filters);

        var propertyRequest = new PropertyRequest
        {
            UserId = request.UserId,
            ListingType = "DEMAND",
            TransactionType = normalizedTransactionType,
            Category = request.PropertyType,
            Title = request.LookingFor.Trim(),
            Status = "LOOKING",
            BudgetMin = minBudget,
            BudgetMax = maxBudget,
            City = "Indore",
            Locality = request.PreferredLocation.Trim(),
            Location = new Point(request.Longitude, request.Latitude) { SRID = 4326 },
            RadiusKm = request.RadiusKm,
            FiltersJson = filtersJson,
            BudgetJson = JsonSerializer.Serialize(new { min = minBudget, max = maxBudget, display = request.Budget }),
            RequiredAreaJson = JsonSerializer.Serialize(new { min = request.RequiredArea, max = request.RequiredArea }),
            PreferredLocationsJson = "[]",
            SearchQueryJson = "{}",
            PropertyTypesJson = "[]",
            PostedAt = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };

        _dbContext.PropertyRequests.Add(propertyRequest);
        await _dbContext.SaveChangesAsync();

        var matchesCount = await _dbContext.PropertyRequests
            .AsNoTracking()
            .CountAsync(other => other.UserId != request.UserId && 
                                 other.ListingType == "SUPPLY" && 
                                 other.Category == propertyRequest.Category && 
                                 other.TransactionType == propertyRequest.TransactionType);

        return new AddRequirementResponseDto
        {
            Success = true,
            Message = "Requirement listed. Matchmaking engine triggered.",
            Data = new AddRequirementDataDto
            {
                Id = propertyRequest.Id.ToString(),
                UserId = propertyRequest.UserId,
                LookingFor = propertyRequest.Title,
                Location = propertyRequest.Locality,
                Budget = request.Budget,
                Status = "Active",
                MatchesFound = matchesCount,
                CreatedAt = propertyRequest.PostedAt
            }
        };
    }

    private static (long min, long max) ParseBudget(string budgetStr)
    {
        if (string.IsNullOrWhiteSpace(budgetStr)) return (0, 0);

        try
        {
            // Clean the string
            var cleaned = budgetStr.Replace("₹", "").Replace(",", "").Replace(" ", "").Replace("/mo", "").Replace("/month", "").ToLowerInvariant();

            // Check for ranges with "-" or "–"
            var parts = cleaned.Split(new[] { '-', '–' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var minVal = ParseBudgetUnit(parts[0]);
                var maxVal = ParseBudgetUnit(parts[1]);
                return (minVal, maxVal);
            }
            else if (parts.Length == 1)
            {
                var val = ParseBudgetUnit(parts[0]);
                return (val, val);
            }
        }
        catch {}

        return (0, 0);
    }

    private static long ParseBudgetUnit(string val)
    {
        double multiplier = 1;
        if (val.Contains("lakh") || val.Contains("l"))
        {
            multiplier = 100000;
            val = val.Replace("lakhs", "").Replace("lakh", "").Replace("l", "");
        }
        else if (val.Contains("crore") || val.Contains("cr") || val.Contains("c"))
        {
            multiplier = 10000000;
            val = val.Replace("crores", "").Replace("crore", "").Replace("cr", "").Replace("c", "");
        }
        else if (val.Contains("k"))
        {
            multiplier = 1000;
            val = val.Replace("k", "");
        }

        if (double.TryParse(val, out var dVal))
        {
            return Convert.ToInt64(dVal * multiplier);
        }
        return 0;
    }
}
