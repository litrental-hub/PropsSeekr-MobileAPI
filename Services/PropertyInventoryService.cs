using System.ComponentModel.DataAnnotations;
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
            "BUY_SELL", "RENTAL"
        };

        if (!allowedTransactionTypes.Contains(request.TransactionType.Trim()))
        {
            throw new ValidationException("Transaction type must be either BUY_SELL or RENTAL.");
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
}
