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
        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue)
        {
            return new MyRequirementsResponseDto
            {
                Success = true,
                Metadata = new MetadataDto
                {
                    Page = pagination.Page > 0 ? pagination.Page : 1,
                    Limit = pagination.Limit > 0 ? pagination.Limit : 20
                }
            };
        }

        return await GetRequirementsAsync(brokerId.Value, pagination, transactionType);
    }

    public Task<MyRequirementsResponseDto> GetAllRequirementsAsync(
        PaginationDto pagination,
        string? transactionType = null) =>
        GetRequirementsAsync(null, pagination, transactionType);

    private async Task<MyRequirementsResponseDto> GetRequirementsAsync(
        int? brokerId,
        PaginationDto pagination,
        string? transactionType)
    {
        var pageNumber = pagination.Page > 0 ? pagination.Page : 1;
        var limit = pagination.Limit > 0 ? pagination.Limit : 20;
        var skip = (pageNumber - 1) * limit;

        var query = _dbContext.PropertyRequests
            .AsNoTracking()
            .Where(p => p.ListingType == "DEMAND");

        // Legacy PropertyRequests are retained for historical data only; they
        // are not the matching source of truth and must not appear in /mine.
        query = query.Where(_ => false);

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

        // Do not materialize the legacy PropertyRequests projection. Canonical
        // Requirements is the sole source for this endpoint and for matching.
        var requirements = new List<PropertyRequest>();

        var legacyResponseItems = requirements.Select(p => {
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

        var canonicalQuery = _dbContext.Requirements.AsNoTracking();
        if (brokerId.HasValue)
            canonicalQuery = canonicalQuery.Where(requirement => requirement.BrokerId == brokerId.Value);

        if (!string.IsNullOrWhiteSpace(transactionType))
        {
            {
                var normalizedTransactionType = transactionType.Trim().ToUpperInvariant().Replace('-', '_').Replace('/', '_');
                if (normalizedTransactionType is "RENT" or "RENTAL" or "LEASE")
                {
                    canonicalQuery = canonicalQuery.Where(requirement =>
                        requirement.RequirementType == "RENT" ||
                        requirement.RequirementType == "RENTAL" ||
                        requirement.RequirementType == "LEASE");
                }
                else if (normalizedTransactionType is "BUY_SELL" or "BUY" or "SELL" or "SALE" or "PURCHASE")
                {
                    canonicalQuery = canonicalQuery.Where(requirement =>
                        requirement.RequirementType == "BUY" ||
                        requirement.RequirementType == "SELL" ||
                        requirement.RequirementType == "BUY_SELL" ||
                        requirement.RequirementType == "SALE" ||
                        requirement.RequirementType == "PURCHASE");
                }
                else
                {
                    throw new ArgumentException("transactionType must be RENTAL or BUY_SELL.", nameof(transactionType));
                }
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

        var responseItems = canonical.Select(requirement =>
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
                        Min = Convert.ToInt64(requirement.BudgetMin ?? 0),
                        Max = Convert.ToInt64(requirement.Budget ?? 0),
                        DisplayValue = requirement.Budget.HasValue
                            ? requirement.BudgetMin.HasValue
                                ? $"₹{requirement.BudgetMin.Value} - ₹{requirement.Budget.Value}"
                                : $"₹{requirement.Budget.Value}"
                            : "Flexible",
                        Currency = "INR"
                    },
                    PreferredLocation = new LocationDto
                    {
                        City = city,
                        Locality = city,
                        RadiusKm = requirement.RadiusKm ?? 3
                    },
                    RequiredArea = new RequiredAreaDto
                    {
                        Min = Convert.ToInt32(requirement.Size ?? 0),
                        Max = Convert.ToInt32(requirement.SizeMax ?? requirement.Size ?? 0),
                        DisplayValue = requirement.Size.HasValue
                            ? requirement.SizeMax.HasValue
                                ? $"{requirement.Size.Value} - {requirement.SizeMax.Value} SQFT"
                                : $"{requirement.Size.Value}+ SQFT"
                            : string.Empty,
                        Unit = "SQFT"
                    },
                    PreferredProjects = requirement.PreferredProjectNames ?? [],
                    BudgetType = requirement.BudgetType ?? "FIXED",
                    PostedAt = requirement.CreatedAt ?? DateTime.MinValue,
                    Status = NormalizeStatus(requirement.Status)
                };
            }).ToList();

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
                TotalCount = await CountCanonicalRequirementsAsync(brokerId, transactionType),
                Page = pageNumber,
                Limit = limit
            },
            Data = responseItems
        };
    }

    public async Task<CreateRequirementResponseDto> AddRequirementAsync(Guid userId, CreateRequirementRequestDto request)
    {
        var budgetType = InventoryNormalization.BudgetType(request.BudgetType);
        if (budgetType == "FIXED" && request.BudgetMax <= 0)
            throw new ArgumentException("Budget must be greater than zero.");

        if (request.BudgetMin is < 0)
            throw new ArgumentException("Minimum budget cannot be negative.");

        if (request.BudgetMin.HasValue && request.BudgetMax > 0 && request.BudgetMin > request.BudgetMax)
            throw new ArgumentException("Minimum budget cannot exceed maximum budget.");

        if (request.MinimumSize <= 0)
            throw new ArgumentException("Minimum size must be greater than zero.");

        if (request.MaximumSize.HasValue && request.MaximumSize < request.MinimumSize)
            throw new ArgumentException("Maximum size cannot be smaller than minimum size.");

        var preferredLocations = request.PreferredLocations is { Count: > 0 }
            ? request.PreferredLocations
            :
            [
                new PropSeekr.DTOs.Requirements.PreferredLocationDto
                {
                    City = request.City,
                    Locality = request.Locality,
                    Lat = request.Lat,
                    Lng = request.Lng
                }
            ];

        if (preferredLocations.Any(location =>
                string.IsNullOrWhiteSpace(location.City) ||
                string.IsNullOrWhiteSpace(location.Locality)))
            throw new ArgumentException("City and locality are required for every preferred location.");

        if (preferredLocations.Count > 5)
            throw new ArgumentException("A requirement can contain at most five preferred localities.");

        if (preferredLocations.Select(location => location.City.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            throw new ArgumentException("All preferred localities must be in the same city.");

        if (preferredLocations.Any(location =>
                location.Lat is < -90 or > 90 || location.Lng is < -180 or > 180 ||
                (location.Lat == 0 && location.Lng == 0)))
            throw new ArgumentException("Valid GPS coordinates are required for every preferred location.");

        if (request.RadiusKm is <= 0 or > 100)
            throw new ArgumentException("Search radius must be between 0 and 100 km.");

        if (string.IsNullOrWhiteSpace(request.PropertyType))
            throw new ArgumentException("Property type is required.");

        var preferredProjectNames = (request.PreferredProjectNames ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (preferredProjectNames.Length > 5 || preferredProjectNames.Any(value => value.Length > 255))
            throw new ArgumentException("Provide at most five project names, each up to 255 characters.");

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

        Requirement requirement;
        await using (var transaction = await _dbContext.Database.BeginTransactionAsync())
        {
            var masterIds = new List<int>();
            foreach (var location in preferredLocations)
            {
                var masterId = await MasterLocationResolver.ResolveAsync(
                    _dbContext,
                    location.City,
                    location.Locality,
                    location.Lat,
                    location.Lng);
                if (!masterIds.Contains(masterId)) masterIds.Add(masterId);
            }

            requirement = new Requirement
            {
                BrokerId = brokerId,
                Source = "manual",
                RawMessageText = BuildRequirementMatchText(request),
                RequirementType = normalizedTransactionType == "RENTAL" ? "RENT" : "BUY",
                PropertyType = InventoryNormalization.PropertyType(request.PropertyType),
                Configurations = InventoryNormalization.Configurations(request.Configurations),
                PreferredLocalityIds = masterIds.ToArray(),
                Budget = request.BudgetMax > 0 ? request.BudgetMax : null,
                BudgetMin = request.BudgetMin,
                BudgetUnit = normalizedTransactionType == "RENTAL" ? "PER_MONTH" : "TOTAL",
                BudgetType = budgetType,
                Size = request.MinimumSize,
                SizeMax = request.MaximumSize,
                RadiusKm = request.RadiusKm,
                PreferredProjectNames = preferredProjectNames,
                FurnishingPref = InventoryNormalization.Furnishing(request.FurnishingPreference),
                FacingPref = InventoryNormalization.Facing(request.FacingPreference),
                Status = "active",
                ExpiresAt = DateTime.UtcNow.AddDays(30),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                FreshnessUpdatedAt = DateTime.UtcNow,
                City = preferredLocations[0].City.Trim(),
                PostedBy = "BROKER"
            };

            _dbContext.Requirements.Add(requirement);
            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        IReadOnlyList<int> matches = [];
        var embeddingCompleted = true;
        try
        {
            await _matchingPipeline.TriggerForRequirementAsync(requirement.Id);
            matches = await _dbContext.Matches
                .AsNoTracking()
                .Where(match => match.RequirementId == requirement.Id && match.Status == "MATCHED")
                .Select(match => match.Id)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            embeddingCompleted = false;
            _logger.LogError(ex, "Embedding and matching pipeline failed for requirement {RequirementId}", requirement.Id);
        }

        return new CreateRequirementResponseDto
        {
            Success = true,
            RequirementId = requirement.Id.ToString(),
            MatchCount = matches.Count,
            EmbeddingCompleted = embeddingCompleted,
            Message = embeddingCompleted
                ? "Requirement posted successfully. Gemini embedding and matching completed."
                : "Requirement posted, but Gemini embedding or matching failed. Check API logs and retry the embedding."
        };
    }

    public async Task<CreateRequirementResponseDto> UpdateRequirementAsync(Guid userId, int requirementId, CreateRequirementRequestDto request)
    {
        var budgetType = InventoryNormalization.BudgetType(request.BudgetType);
        if (budgetType == "FIXED" && request.BudgetMax <= 0)
            throw new ArgumentException("Budget must be greater than zero.");
        if (request.BudgetMin is < 0)
            throw new ArgumentException("Minimum budget cannot be negative.");
        if (request.BudgetMin.HasValue && request.BudgetMax > 0 && request.BudgetMin > request.BudgetMax)
            throw new ArgumentException("Minimum budget cannot exceed maximum budget.");
        if (request.MinimumSize <= 0)
            throw new ArgumentException("Minimum size must be greater than zero.");
        if (request.MaximumSize.HasValue && request.MaximumSize < request.MinimumSize)
            throw new ArgumentException("Maximum size cannot be smaller than minimum size.");

        var preferredLocations = request.PreferredLocations is { Count: > 0 }
            ? request.PreferredLocations
            :
            [
                new PropSeekr.DTOs.Requirements.PreferredLocationDto
                {
                    City = request.City,
                    Locality = request.Locality,
                    Lat = request.Lat,
                    Lng = request.Lng
                }
            ];
        if (preferredLocations.Any(location =>
                string.IsNullOrWhiteSpace(location.City) || string.IsNullOrWhiteSpace(location.Locality)))
            throw new ArgumentException("City and locality are required for every preferred location.");
        if (preferredLocations.Count > 5)
            throw new ArgumentException("A requirement can contain at most five preferred localities.");
        if (preferredLocations.Select(location => location.City.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            throw new ArgumentException("All preferred localities must be in the same city.");
        if (preferredLocations.Any(location =>
                location.Lat is < -90 or > 90 || location.Lng is < -180 or > 180 ||
                (location.Lat == 0 && location.Lng == 0)))
            throw new ArgumentException("Valid GPS coordinates are required for every preferred location.");
        if (request.RadiusKm is <= 0 or > 100)
            throw new ArgumentException("Search radius must be between 0 and 100 km.");
        if (string.IsNullOrWhiteSpace(request.PropertyType))
            throw new ArgumentException("Property type is required.");

        var preferredProjectNames = (request.PreferredProjectNames ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (preferredProjectNames.Length > 5 || preferredProjectNames.Any(value => value.Length > 255))
            throw new ArgumentException("Provide at most five project names, each up to 255 characters.");

        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId)
            ?? throw new KeyNotFoundException("No broker profile is linked to this account.");
        var requirement = await _dbContext.Requirements.FirstOrDefaultAsync(item => item.Id == requirementId)
            ?? throw new KeyNotFoundException("Requirement not found.");
        if (requirement.BrokerId != brokerId)
            throw new UnauthorizedAccessException("You can only edit your own requirements.");

        var normalizedTransactionType = request.TransactionType.Trim().ToUpperInvariant();

        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        var masterIds = new List<int>();
        foreach (var location in preferredLocations)
        {
            var masterId = await MasterLocationResolver.ResolveAsync(
                _dbContext,
                location.City,
                location.Locality,
                location.Lat,
                location.Lng);
            if (!masterIds.Contains(masterId)) masterIds.Add(masterId);
        }

        requirement.RawMessageText = BuildRequirementMatchText(request);
        requirement.RequirementType = normalizedTransactionType == "RENTAL" ? "RENT" : "BUY";
        requirement.PropertyType = InventoryNormalization.PropertyType(request.PropertyType);
        requirement.Configurations = InventoryNormalization.Configurations(request.Configurations);
        requirement.PreferredLocalityIds = masterIds.ToArray();
        requirement.Budget = request.BudgetMax > 0 ? request.BudgetMax : null;
        requirement.BudgetMin = request.BudgetMin;
        requirement.BudgetUnit = normalizedTransactionType == "RENTAL" ? "PER_MONTH" : "TOTAL";
        requirement.BudgetType = budgetType;
        requirement.Size = request.MinimumSize;
        requirement.SizeMax = request.MaximumSize;
        requirement.RadiusKm = request.RadiusKm;
        requirement.PreferredProjectNames = preferredProjectNames;
        requirement.FurnishingPref = InventoryNormalization.Furnishing(request.FurnishingPreference);
        requirement.FacingPref = InventoryNormalization.Facing(request.FacingPreference);
        requirement.City = preferredLocations[0].City.Trim();
        requirement.UpdatedAt = DateTime.UtcNow;

        _dbContext.Requirements.Update(requirement);
        await _dbContext.SaveChangesAsync();

        await transaction.CommitAsync();

        IReadOnlyList<int> matches = [];
        var embeddingCompleted = true;
        try
        {
            await _matchingPipeline.TriggerForRequirementAsync(requirement.Id);
            matches = await _dbContext.Matches
                .AsNoTracking()
                .Where(match => match.RequirementId == requirement.Id && match.Status == "MATCHED")
                .Select(match => match.Id)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            embeddingCompleted = false;
            _logger.LogError(ex, "Embedding and matching pipeline failed for requirement update {RequirementId}", requirement.Id);
        }

        return new CreateRequirementResponseDto
        {
            Success = true,
            RequirementId = requirement.Id.ToString(),
            MatchCount = matches.Count,
            EmbeddingCompleted = embeddingCompleted,
            Message = embeddingCompleted
                ? "Requirement updated successfully."
                : "Requirement updated, but matching pipeline encountered an issue."
        };
    }

    private async Task<int> CountCanonicalRequirementsAsync(int? brokerId, string? transactionType)
    {
        var query = _dbContext.Requirements.AsNoTracking();
        if (brokerId.HasValue)
            query = query.Where(requirement => requirement.BrokerId == brokerId.Value);
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
            string.Join(" ", (request.PreferredLocations ?? []).Select(location => $"{location.Locality} {location.City}")),
            request.MinimumSize > 0 ? $"{request.MinimumSize} sqft" : null,
            request.MaximumSize.HasValue ? $"maximum {request.MaximumSize.Value} sqft" : null,
            request.BudgetMin.HasValue ? $"minimum budget {request.BudgetMin.Value}" : null,
            request.BudgetMax > 0
                ? $"maximum budget {request.BudgetMax} {(string.Equals(request.TransactionType, "RENTAL", StringComparison.OrdinalIgnoreCase) ? "per month" : "total")}"
                : null,
            request.BudgetType,
            request.RadiusKm > 0 ? $"within {request.RadiusKm} km" : null,
            string.Join(" ", (request.PreferredProjectNames ?? []).Where(value => !string.IsNullOrWhiteSpace(value))),
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
