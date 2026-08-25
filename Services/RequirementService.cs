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
    private readonly IBrokerIdentityService _brokerIdentityService;
    private readonly IMatchingPipelineService _matchingPipeline;
    private readonly ILogger<RequirementService> _logger;

    public RequirementService(
        AppDbContext dbContext,
        IBrokerIdentityService brokerIdentityService,
        IMatchingPipelineService matchingPipeline,
        ILogger<RequirementService> logger)
    {
        _dbContext = dbContext;
        _brokerIdentityService = brokerIdentityService;
        _matchingPipeline = matchingPipeline;
        _logger = logger;
    }

    public async Task<MyRequirementsResponseDto> GetMyRequirementsAsync(
        Guid userId,
        PaginationDto pagination,
        string? transactionType = null)
    {
        var pageNumber = pagination.Page > 0 ? pagination.Page : 1;
        var limit = pagination.Limit > 0 ? pagination.Limit : 20;
        var skip = (pageNumber - 1) * limit;

        var query = _dbContext.PropertyRequests
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.ListingType == "DEMAND");

        if (!string.IsNullOrWhiteSpace(transactionType))
        {
            var normalizedTransactionType = transactionType.Trim().ToUpperInvariant().Replace('-', '_').Replace('/', '_');
            if (normalizedTransactionType is "RENT" or "RENTAL" or "LEASE")
            {
                query = query.Where(p => p.TransactionType == "RENTAL" || p.TransactionType == "RENT");
            }
            else if (normalizedTransactionType is "BUY_SELL" or "BUY" or "SELL" or "SALE")
            {
                query = query.Where(p =>
                    p.TransactionType == "BUY" ||
                    p.TransactionType == "SELL" ||
                    p.TransactionType == "BUY_SELL" ||
                    p.TransactionType == "SALE");
            }
            else
            {
                throw new ArgumentException("transactionType must be RENTAL or BUY_SELL.", nameof(transactionType));
            }
        }

        var requirements = await query.OrderByDescending(p => p.PostedAt).ToListAsync();

        var responseItems = requirements.Select(p => {
            var requiredArea = p.RequiredAreaJson != null ? DeserializeJson<RequiredAreaDto>(p.RequiredAreaJson) : null;
            return new RequirementListItemDto
            {
                Id = p.Id.ToString(),
                RequirementId = p.Id.ToString(),
                Description = p.Title,
                TransactionType = p.TransactionType,
                Category = p.Category,
                PropertyType = p.Category,
                Locality = p.Locality,
                Location = p.Locality,
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

        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (brokerId.HasValue)
        {
            var canonicalQuery = _dbContext.Requirements
                .AsNoTracking()
                .Where(requirement => requirement.BrokerId == brokerId.Value);

            if (!string.IsNullOrWhiteSpace(transactionType))
            {
                var normalizedTransactionType = transactionType.Trim().ToUpperInvariant().Replace('-', '_').Replace('/', '_');
                if (normalizedTransactionType is "RENT" or "RENTAL" or "LEASE")
                {
                    canonicalQuery = canonicalQuery.Where(requirement =>
                        requirement.RequirementType == "RENT" ||
                        requirement.RequirementType == "RENTAL" ||
                        requirement.RequirementType == "LEASE");
                }
                else
                {
                    canonicalQuery = canonicalQuery.Where(requirement =>
                        requirement.RequirementType == "BUY" ||
                        requirement.RequirementType == "SELL" ||
                        requirement.RequirementType == "BUY_SELL" ||
                        requirement.RequirementType == "SALE" ||
                        requirement.RequirementType == "PURCHASE");
                }
            }

            var canonical = await canonicalQuery.OrderByDescending(requirement => requirement.CreatedAt).ToListAsync();
            var canonicalIds = canonical.Select(requirement => requirement.Id).ToArray();
            var matchCounts = canonicalIds.Length == 0
                ? new Dictionary<int, int>()
                : await _dbContext.Matches.AsNoTracking()
                    .Where(match => canonicalIds.Contains(match.RequirementId))
                    .GroupBy(match => match.RequirementId)
                    .Select(group => new { RequirementId = group.Key, Count = group.Count() })
                    .ToDictionaryAsync(row => row.RequirementId, row => row.Count);

            responseItems.AddRange(canonical.Select(requirement =>
            {
                var id = requirement.Id.ToString();
                var configuration = requirement.Configurations?.FirstOrDefault() ?? string.Empty;
                var propertyType = requirement.PropertyType ?? "Property";
                var action = IsRentalRequirement(requirement.RequirementType) ? "Rent" : "Buy";
                var city = requirement.City ?? "Location not specified";
                return new RequirementListItemDto
                {
                    Id = id,
                    RequirementId = id,
                    Description = $"Wants to {action} {configuration} {propertyType}".Replace("  ", " ").Trim(),
                    TransactionType = IsRentalRequirement(requirement.RequirementType) ? "RENTAL" : "BUY_SELL",
                    Category = propertyType,
                    PropertyType = propertyType,
                    Configuration = configuration,
                    Locality = city,
                    Location = city,
                    MatchesFound = matchCounts.GetValueOrDefault(requirement.Id),
                    Budget = new BudgetResponseDto
                    {
                        Min = 0,
                        Max = Convert.ToInt64(requirement.Budget ?? 0),
                        DisplayValue = requirement.Budget.HasValue ? $"₹{requirement.Budget.Value}" : string.Empty,
                        Currency = "INR"
                    },
                    PreferredLocation = new LocationDto { City = city, Locality = city },
                    RequiredArea = new RequiredAreaDto
                    {
                        Min = Convert.ToInt32(requirement.Size ?? 0),
                        Max = Convert.ToInt32(requirement.Size ?? 0),
                        DisplayValue = requirement.Size.HasValue ? $"{requirement.Size.Value} SQFT" : string.Empty,
                        Unit = "SQFT"
                    },
                    PostedAt = requirement.CreatedAt ?? DateTime.MinValue,
                    Status = NormalizeStatus(requirement.Status)
                };
            }));
        }

        responseItems = responseItems
            .OrderByDescending(item => item.PostedAt)
            .Skip(skip)
            .Take(limit)
            .ToList();

        return new MyRequirementsResponseDto
        {
            Success = true,
            Metadata = new MetadataDto
            {
                TotalCount = requirements.Count + (brokerId.HasValue
                    ? await CountCanonicalRequirementsAsync(brokerId.Value, transactionType)
                    : 0),
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

        if (string.IsNullOrWhiteSpace(request.PropertyType))
            throw new ArgumentException("Property type is required.");

        var normalizedTransactionType = string.IsNullOrWhiteSpace(request.TransactionType) ? string.Empty : request.TransactionType.Trim().ToUpperInvariant();
        if (normalizedTransactionType == "BUY_SELL" || normalizedTransactionType == "BUY" || normalizedTransactionType == "PURCHASE")
        {
            normalizedTransactionType = "BUY";
        }
        else if (normalizedTransactionType != "RENTAL")
        {
            throw new ArgumentException("Transaction type must be BUY, BUY_SELL, or RENTAL.");
        }

        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId)
            ?? throw new KeyNotFoundException("No broker profile is linked to this account.");

        var requirement = new Requirement
        {
            BrokerId = brokerId,
            Source = "manual",
            // The embedding and locality fallback need the structured form values too,
            // not only an optional free-text note.
            RawMessageText = BuildRequirementMatchText(request),
            RequirementType = normalizedTransactionType == "RENTAL" ? "RENT" : "BUY",
            PropertyType = request.PropertyType.Trim().ToUpperInvariant(),
            Configurations = request.Configurations.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray(),
            Budget = request.BudgetMax,
            BudgetUnit = "TOTAL",
            Size = request.MinimumSize,
            FurnishingPref = request.FurnishingPreference,
            FacingPref = request.FacingPreference,
            Status = "active",
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            FreshnessUpdatedAt = DateTime.UtcNow,
            City = request.City,
            PostedBy = "BROKER"
        };

        _dbContext.Requirements.Add(requirement);
        await _dbContext.SaveChangesAsync();

        IReadOnlyList<int> matches = [];
        try
        {
            await _matchingPipeline.TriggerForRequirementAsync(requirement.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding and matching pipeline failed to start for requirement {RequirementId}", requirement.Id);
        }

        return new CreateRequirementResponseDto
        {
            Success = true,
            RequirementId = requirement.Id.ToString(),
            MatchCount = matches.Count,
            Message = "Requirement successfully posted. Embedding and matching have started."
        };
    }

    private async Task<int> CountCanonicalRequirementsAsync(int brokerId, string? transactionType)
    {
        var query = _dbContext.Requirements.AsNoTracking().Where(requirement => requirement.BrokerId == brokerId);
        if (string.IsNullOrWhiteSpace(transactionType)) return await query.CountAsync();
        var rental = IsRentalRequirement(transactionType);
        return await query.CountAsync(requirement => rental
            ? requirement.RequirementType == "RENT" || requirement.RequirementType == "RENTAL" || requirement.RequirementType == "LEASE"
            : requirement.RequirementType == "BUY" || requirement.RequirementType == "SELL" || requirement.RequirementType == "BUY_SELL" || requirement.RequirementType == "SALE" || requirement.RequirementType == "PURCHASE");
    }

    private static bool IsRentalRequirement(string? value) =>
        value?.Trim().ToUpperInvariant() is "RENT" or "RENTAL" or "LEASE";

    private static string NormalizeStatus(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Under Review"
            : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private static string BuildRequirementMatchText(CreateRequirementRequestDto request)
    {
        var parts = new[]
        {
            request.Description,
            request.TransactionType,
            string.Join(" ", request.Configurations.Where(value => !string.IsNullOrWhiteSpace(value))),
            request.PropertyType,
            request.Locality,
            request.City,
            request.MinimumSize > 0 ? $"{request.MinimumSize} sqft" : null,
            request.BudgetMax > 0 ? $"budget {request.BudgetMax} total" : null,
            request.FurnishingPreference,
            request.FacingPreference,
            request.AdditionalNotes
        };

        return string.Join(". ", parts.Where(value => !string.IsNullOrWhiteSpace(value)));
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
