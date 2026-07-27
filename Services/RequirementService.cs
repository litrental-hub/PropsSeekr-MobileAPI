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

        var propertyRequest = new PropertyRequest
        {
            UserId = userId,
            ListingType = "DEMAND",
            TransactionType = request.TransactionType,
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
}
