using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using PropSeekr.Data;
using PropSeekr.DTOs.Inventory;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public class PropertyInventoryService : IPropertyInventoryService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<PropertyInventoryService> _logger;

    public PropertyInventoryService(AppDbContext dbContext, ILogger<PropertyInventoryService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<GetMyPropertyListingsResponseDto> GetMyPropertyListingsAsync(Guid userId, int page, int limit)
    {
        if (page < 1) page = 1;
        if (limit < 1 || limit > 100) limit = 20;

        var query = _dbContext.PropertyRequests
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.ListingType == "SUPPLY")
            .OrderByDescending(p => p.PostedAt);

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(p => new PropertyListingDto
            {
                Id = p.Id.ToString(),
                Title = p.Title,
                ListingType = NormalizeListingType(p.ListingType, p.Status),
                TransactionType = p.TransactionType,
                Category = p.Category,
                Price = p.BudgetMin ?? 0,
                BuiltUpSize = 0,
                City = p.City,
                Locality = p.Locality,
                Status = p.Status,
                CreatedAt = p.PostedAt,
                UpdatedAt = p.ModifiedDate
            })
            .ToListAsync();

        return new GetMyPropertyListingsResponseDto
        {
            Success = true,
            TotalCount = totalCount,
            Page = page,
            Limit = limit,
            Data = items
        };
    }

    public async Task<PropertyListingDto> CreatePropertyListingAsync(Guid userId, CreatePropertyListingRequestDto request)
    {
        ValidateRequest(request);

        var normalizedTransactionType = NormalizeValue(request.TransactionType);
        if (normalizedTransactionType == "BUY_SELL" || normalizedTransactionType == "SALE")
        {
            normalizedTransactionType = "SELL";
        }
        var normalizedCategory = NormalizeValue(request.Category);
        var normalizedStatus = NormalizeValue(request.Status);

        var propertyRequest = new PropertyRequest
        {
            UserId = userId,
            Title = request.Title.Trim(),
            ListingType = "SUPPLY",
            TransactionType = normalizedTransactionType,
            Category = normalizedCategory,
            Status = string.IsNullOrWhiteSpace(normalizedStatus) ? "ACTIVE" : normalizedStatus,
            City = request.City.Trim(),
            Locality = request.Locality.Trim(),
            BudgetMin = Convert.ToInt64(Math.Round(request.AskingPrice, 0)),
            BudgetMax = Convert.ToInt64(Math.Round(request.AskingPrice, 0)),
            PreferredLocationsJson = "[]",
            BudgetJson = System.Text.Json.JsonSerializer.Serialize(new { min = request.AskingPrice, max = request.AskingPrice }),
            RequiredAreaJson = System.Text.Json.JsonSerializer.Serialize(new { min = request.BuiltUpSize, max = request.BuiltUpSize }),
            FiltersJson = "{}",
            SearchQueryJson = "{}",
            PropertyTypesJson = "[]",
            Location = new Point(request.Longitude, request.Latitude) { SRID = 4326 },
            PostedAt = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };

        _dbContext.PropertyRequests.Add(propertyRequest);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created property listing {PropertyId} for user {UserId}", propertyRequest.Id, userId);

        return new PropertyListingDto
        {
            Id = propertyRequest.Id.ToString(),
            Title = propertyRequest.Title,
            ListingType = NormalizeListingType(propertyRequest.ListingType, propertyRequest.Status),
            TransactionType = propertyRequest.TransactionType,
            Category = propertyRequest.Category,
            Price = propertyRequest.BudgetMin ?? 0,
            BuiltUpSize = Convert.ToDecimal(request.BuiltUpSize),
            City = propertyRequest.City,
            Locality = propertyRequest.Locality,
            Status = propertyRequest.Status,
            CreatedAt = propertyRequest.PostedAt,
            UpdatedAt = propertyRequest.ModifiedDate
        };
    }

    private static void ValidateRequest(CreatePropertyListingRequestDto request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ValidationException("Property title is required.");
        }

        if (string.IsNullOrWhiteSpace(request.TransactionType))
        {
            throw new ValidationException("Transaction type is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Category))
        {
            throw new ValidationException("Category is required.");
        }

        if (string.IsNullOrWhiteSpace(request.City))
        {
            throw new ValidationException("City is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Locality))
        {
            throw new ValidationException("Locality is required.");
        }

        if (request.AskingPrice <= 0)
        {
            throw new ValidationException("Asking price must be greater than zero.");
        }

        if (request.BuiltUpSize <= 0)
        {
            throw new ValidationException("Built-up size must be greater than zero.");
        }

        if (request.Latitude is < -90 or > 90)
        {
            throw new ValidationException("Latitude must be between -90 and 90.");
        }

        if (request.Longitude is < -180 or > 180)
        {
            throw new ValidationException("Longitude must be between -180 and 180.");
        }

        var allowedTransactionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BUY_SELL", "SALE", "SELL", "RENTAL"
        };

        if (!allowedTransactionTypes.Contains(request.TransactionType.Trim()))
        {
            throw new ValidationException("Transaction type must be SELL, SALE, or RENTAL.");
        }

        var allowedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ACTIVE", "DRAFT"
        };

        if (!string.IsNullOrWhiteSpace(request.Status) && !allowedStatuses.Contains(request.Status.Trim()))
        {
            throw new ValidationException("Status must be ACTIVE or DRAFT.");
        }
    }

    private static string NormalizeValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }

    private static string NormalizeListingType(string? listingType, string? status)
    {
        if (!string.IsNullOrWhiteSpace(listingType))
        {
            return listingType.Trim().ToUpperInvariant();
        }

        return string.Equals(status, "LOOKING", StringComparison.OrdinalIgnoreCase) ? "DEMAND" : "SUPPLY";
    }

    public async Task<MyPropertiesResponseDto> GetMyPropertiesWithMetricsAsync(Guid userId, string? status, int page, int limit)
    {
        if (page < 1) page = 1;
        if (limit < 1 || limit > 100) limit = 20;

        var query = _dbContext.PropertyRequests
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.ListingType == "SUPPLY");

        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(p => p.Status.ToLower() == status.ToLower());
        }

        var totalCount = await query.CountAsync();
        var activeCount = await _dbContext.PropertyRequests
            .AsNoTracking()
            .CountAsync(p => p.UserId == userId && p.ListingType == "SUPPLY" && p.Status.ToLower() == "active");

        var items = await query
            .OrderByDescending(p => p.PostedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var data = new List<MyPropertyItemDto>();
        foreach (var p in items)
        {
            var matchesCount = await _dbContext.PropertyRequests
                .AsNoTracking()
                .CountAsync(other => other.UserId != userId && 
                                     other.ListingType == "DEMAND" && 
                                     other.Category == p.Category && 
                                     other.TransactionType == p.TransactionType);

            // Default fallback values
            string landmarkStreet = string.Empty;
            int sqFeet = 0;
            string availableFrom = string.Empty;
            long monthlyRent = 0;
            long securityDeposit = 0;
            long maintenanceCharges = 0;
            int floorNumber = 0;
            int totalFloors = 0;
            string furnishingStatus = string.Empty;
            int bathrooms = 0;
            int balconies = 0;
            string facingDirection = string.Empty;
            var amenities = new List<string>();
            string dietPreferences = string.Empty;
            string petPolicy = string.Empty;
            int minimumLeasePeriod = 0;
            string policeVerificationAllowed = "no";
            var photos = new List<string>();

            // Parse SqFeet from RequiredAreaJson if present
            try
            {
                if (!string.IsNullOrEmpty(p.RequiredAreaJson))
                {
                    using var areaDoc = JsonDocument.Parse(p.RequiredAreaJson);
                    if (areaDoc.RootElement.TryGetProperty("min", out var minAreaProp))
                    {
                        sqFeet = minAreaProp.GetInt32();
                    }
                }
            }
            catch {}

            try
            {
                if (!string.IsNullOrEmpty(p.FiltersJson))
                {
                    using var doc = JsonDocument.Parse(p.FiltersJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("landmarkStreet", out var landmarkProp)) landmarkStreet = landmarkProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("sqFeet", out var sqFeetProp)) sqFeet = sqFeetProp.GetInt32();
                    if (root.TryGetProperty("availableFrom", out var availProp)) availableFrom = availProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("monthlyRent", out var rentProp)) monthlyRent = rentProp.GetInt64();
                    if (root.TryGetProperty("securityDeposit", out var depProp)) securityDeposit = depProp.GetInt64();
                    if (root.TryGetProperty("maintenanceCharges", out var maintProp)) maintenanceCharges = maintProp.GetInt64();
                    if (root.TryGetProperty("floorNumber", out var floorProp)) floorNumber = floorProp.GetInt32();
                    if (root.TryGetProperty("totalFloors", out var totalFloorsProp)) totalFloors = totalFloorsProp.GetInt32();
                    if (root.TryGetProperty("furnishingStatus", out var furnProp)) furnishingStatus = furnProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("bathrooms", out var bathProp)) bathrooms = bathProp.GetInt32();
                    if (root.TryGetProperty("balconies", out var balcProp)) balconies = balcProp.GetInt32();
                    if (root.TryGetProperty("facingDirection", out var faceProp)) facingDirection = faceProp.GetString() ?? string.Empty;
                    
                    if (root.TryGetProperty("amenities", out var amenProp) && amenProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in amenProp.EnumerateArray())
                        {
                            amenities.Add(item.GetString() ?? string.Empty);
                        }
                    }
                    if (root.TryGetProperty("dietPreferences", out var dietProp)) dietPreferences = dietProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("petPolicy", out var petProp)) petPolicy = petProp.GetString() ?? string.Empty;
                    if (root.TryGetProperty("minimumLeasePeriod", out var leaseProp)) minimumLeasePeriod = leaseProp.GetInt32();
                    if (root.TryGetProperty("policeVerificationAllowed", out var policeProp)) policeVerificationAllowed = policeProp.GetString() ?? "no";
                    
                    if (root.TryGetProperty("photos", out var photosProp) && photosProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in photosProp.EnumerateArray())
                        {
                            photos.Add(item.GetString() ?? string.Empty);
                        }
                    }
                }
            }
            catch {}

            data.Add(new MyPropertyItemDto
            {
                Id = p.Id.ToString(),
                UserId = p.UserId,
                Title = p.Title,
                TransactionType = p.TransactionType == "SELL" ? "BUY/SELL" : p.TransactionType,
                PropertyType = p.Category,
                City = p.City,
                Area = p.Locality,
                LandmarkStreet = landmarkStreet,
                SqFeet = sqFeet,
                AvailableFrom = availableFrom,
                MonthlyRent = monthlyRent,
                SecurityDeposit = securityDeposit,
                MaintenanceCharges = maintenanceCharges,
                FloorNumber = floorNumber,
                TotalFloors = totalFloors,
                FurnishingStatus = furnishingStatus,
                Bathrooms = bathrooms,
                Balconies = balconies,
                FacingDirection = facingDirection,
                Amenities = amenities,
                DietPreferences = dietPreferences,
                PetPolicy = petPolicy,
                MinimumLeasePeriod = minimumLeasePeriod,
                PoliceVerificationAllowed = policeVerificationAllowed,
                Photos = photos,
                Latitude = p.Location?.Y ?? 0,
                Longitude = p.Location?.X ?? 0,
                Status = p.Status,
                Views = 0,
                Matches = matchesCount,
                CreatedAt = p.PostedAt,
                UpdatedAt = p.ModifiedDate
            });
        }

        return new MyPropertiesResponseDto
        {
            Success = true,
            Page = page,
            Limit = limit,
            TotalCount = totalCount,
            ActiveCount = activeCount,
            Data = data
        };
    }

    public async Task<AddPropertyResponseDto> AddPropertyAsync(AddPropertyRequestDto request)
    {
        var normalizedTransactionType = request.TransactionType.Trim().ToUpperInvariant();
        if (normalizedTransactionType == "BUY_SELL" || normalizedTransactionType == "SALE" || normalizedTransactionType == "SELL")
        {
            normalizedTransactionType = "SELL";
        }
        else
        {
            normalizedTransactionType = "RENTAL";
        }

        var filters = new Dictionary<string, object>
        {
            { "landmarkStreet", request.LandmarkStreet ?? string.Empty },
            { "sqFeet", request.SqFeet },
            { "availableFrom", request.AvailableFrom ?? string.Empty },
            { "monthlyRent", request.MonthlyRent },
            { "securityDeposit", request.SecurityDeposit },
            { "maintenanceCharges", request.MaintenanceCharges },
            { "floorNumber", request.FloorNumber },
            { "totalFloors", request.TotalFloors },
            { "furnishingStatus", request.FurnishingStatus },
            { "bathrooms", request.Bathrooms },
            { "balconies", request.Balconies },
            { "facingDirection", request.FacingDirection },
            { "amenities", request.Amenities },
            { "dietPreferences", request.DietPreferences },
            { "petPolicy", request.PetPolicy },
            { "minimumLeasePeriod", request.MinimumLeasePeriod },
            { "policeVerificationAllowed", request.PoliceVerificationAllowed },
            { "photos", request.Photos }
        };

        var filtersJson = JsonSerializer.Serialize(filters);

        var propertyRequest = new PropertyRequest
        {
            UserId = request.UserId,
            Title = request.Title.Trim(),
            ListingType = "SUPPLY",
            TransactionType = normalizedTransactionType,
            Category = request.PropertyType,
            Status = "Active",
            City = request.City.Trim(),
            Locality = request.Area.Trim(),
            BudgetMin = request.MonthlyRent,
            BudgetMax = request.MonthlyRent,
            PreferredLocationsJson = "[]",
            BudgetJson = JsonSerializer.Serialize(new { min = request.MonthlyRent, max = request.MonthlyRent }),
            RequiredAreaJson = JsonSerializer.Serialize(new { min = request.SqFeet, max = request.SqFeet }),
            FiltersJson = filtersJson,
            SearchQueryJson = "{}",
            PropertyTypesJson = "[]",
            Location = new Point(request.Longitude, request.Latitude) { SRID = 4326 },
            PostedAt = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };

        _dbContext.PropertyRequests.Add(propertyRequest);
        await _dbContext.SaveChangesAsync();

        var matchesCount = await _dbContext.PropertyRequests
            .AsNoTracking()
            .CountAsync(other => other.UserId != request.UserId && 
                                 other.ListingType == "DEMAND" && 
                                 other.Category == propertyRequest.Category && 
                                 other.TransactionType == propertyRequest.TransactionType);

        return new AddPropertyResponseDto
        {
            Success = true,
            Message = "Property successfully listed.",
            Data = new AddPropertyDataDto
            {
                Id = propertyRequest.Id.ToString(),
                UserId = propertyRequest.UserId,
                Title = propertyRequest.Title,
                Location = propertyRequest.Locality,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                Price = normalizedTransactionType == "RENTAL" ? $"₹{request.MonthlyRent:N0} /mo" : $"₹{request.MonthlyRent:N0}",
                Type = request.TransactionType,
                Status = propertyRequest.Status,
                Views = 0,
                Matches = matchesCount,
                CreatedAt = propertyRequest.PostedAt
            }
        };
    }

    public async Task<bool> UpdatePropertyStatusAsync(Guid id, Guid userId, string status)
    {
        var property = await _dbContext.PropertyRequests.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId && p.ListingType == "SUPPLY");
        if (property == null) return false;

        property.Status = status;
        property.ModifiedDate = DateTime.UtcNow;
        _dbContext.PropertyRequests.Update(property);
        await _dbContext.SaveChangesAsync();
        return true;
    }
}
