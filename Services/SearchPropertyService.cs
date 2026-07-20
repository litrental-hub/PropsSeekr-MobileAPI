using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using PropSeekr.Data;
using PropSeekr.DTOs.Search;
using PropSeekr.Services.Interfaces;
using System.Text.Json;

namespace PropSeekr.Services;

public class SearchPropertyService : ISearchPropertyService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<SearchPropertyService> _logger;

    public SearchPropertyService(
        AppDbContext dbContext,
        ILogger<SearchPropertyService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<SearchPropertyResponseDto> SearchPropertiesAsync(
        SearchPropertyRequestDto request,
        Guid userId)
    {
        try
        {
            var query = _dbContext.PropertyRequests.AsQueryable();
            var centre = BuildSearchCentre(request.Location);

            // Filter by transaction type
            if (!string.IsNullOrWhiteSpace(request.TransactionType))
            {
                query = query.Where(p => p.TransactionType == request.TransactionType);
            }

            // Filter by supply / demand listing type
            if (!string.IsNullOrWhiteSpace(request.ListingType))
            {
                query = ApplyListingTypeFilter(query, request.ListingType);
            }

            // Filter by location (city and locality)
            if (!string.IsNullOrWhiteSpace(request.Location.City))
            {
                query = query.Where(p => p.City.ToLower() == request.Location.City.ToLower());
            }

            if (!string.IsNullOrWhiteSpace(request.Location.Locality))
            {
                query = query.Where(p => p.Locality.ToLower() == request.Location.Locality.ToLower());
            }

            if (centre != null && request.Location.RadiusKm > 0)
            {
                var radiusMetres = request.Location.RadiusKm * 1000.0;

                query = query.Where(p =>
                    p.Location != null &&
                    p.Location.IsWithinDistance(centre, radiusMetres)
                );
            }

            // Filter by categories
            if (request.Filters.Categories.Count > 0)
            {
                var categories = request.Filters.Categories.Select(c => c.ToLower()).ToList();
                query = query.Where(p => categories.Contains(p.Category.ToLower()));
            }

            // Filter by property types (stored in PropertyTypesJson)
            if (request.Filters.PropertyTypes.Count > 0)
            {
                var types = request.Filters.PropertyTypes.Select(t => t.ToLower()).ToList();
                query = query.Where(p => types.Any(t => p.PropertyTypesJson.ToLower().Contains(t)));
            }

            // Filter by budget
            if (request.Filters.Budget.Min.HasValue || request.Filters.Budget.Max.HasValue)
            {
                query = FilterByBudget(query, request.Filters.Budget);
            }

            // Filter by search query (title and user info)
            if (!string.IsNullOrWhiteSpace(request.SearchQuery))
            {
                var searchQuery = request.SearchQuery.ToLower();
                query = query.Where(p =>
                    p.Title.ToLower().Contains(searchQuery)
                );
            }

            // Get total count before pagination
            var totalCount = await query.CountAsync();

            // Apply pagination
            var page = request.Pagination.Page <= 0 ? 1 : request.Pagination.Page;
            var limit = request.Pagination.Limit <= 0 ? 20 : request.Pagination.Limit;
            var skip = (page - 1) * limit;

            var propertyRequests = await query
                .Include(p => p.User)
                .ToListAsync();

            if (centre != null)
            {
                propertyRequests = propertyRequests
                    .OrderBy(p => p.Location != null ? GetDistanceKm(p.Location, centre) : double.MaxValue)
                    .Skip(skip)
                    .Take(limit)
                    .ToList();
            }
            else
            {
                propertyRequests = propertyRequests
                    .OrderByDescending(p => p.PostedAt)
                    .Skip(skip)
                    .Take(limit)
                    .ToList();
            }

            // Map to response DTOs
            var responseItems = propertyRequests.Select(pr => MapToResponseDto(pr, centre)).ToList();

            return new SearchPropertyResponseDto
            {
                Success = true,
                Metadata = new MetadataDto
                {
                    TotalCount = totalCount,
                    Page = page,
                    Limit = limit
                },
                Data = responseItems
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error searching properties: {ex.Message}");
            throw;
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

    private static IQueryable<Models.PropertyRequest> ApplyListingTypeFilter(
        IQueryable<Models.PropertyRequest> query,
        string listingType)
    {
        var normalized = listingType.Trim().ToUpperInvariant();

        return normalized switch
        {
            "SUPPLY" => query.Where(p =>
                p.ListingType.ToLower() == "supply" ||
                (string.IsNullOrWhiteSpace(p.ListingType) && p.Status.ToLower() == "active")),
            "DEMAND" => query.Where(p =>
                p.ListingType.ToLower() == "demand" ||
                (string.IsNullOrWhiteSpace(p.ListingType) && p.Status.ToLower() == "looking")),
            _ => query
        };
    }

    private IQueryable<Models.PropertyRequest> FilterByBudget(
        IQueryable<Models.PropertyRequest> query,
        BudgetFilterDto budget)
    {
        if (budget == null)
            return query;

        if (budget.Min.HasValue)
        {
            query = query.Where(p => !p.BudgetMax.HasValue || p.BudgetMax >= budget.Min.Value);
        }

        if (budget.Max.HasValue)
        {
            query = query.Where(p => !p.BudgetMin.HasValue || p.BudgetMin <= budget.Max.Value);
        }

        return query;
    }

    private PropertySearchResponseItemDto MapToResponseDto(
        Models.PropertyRequest pr,
        Point? centre = null)
    {
        var responseItem = new PropertySearchResponseItemDto
        {
            Id = pr.Id.ToString(),
            Status = pr.Status,
            IsAvailable = string.Equals(pr.Status?.Trim(), "ACTIVE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pr.ListingType?.Trim(), "SUPPLY", StringComparison.OrdinalIgnoreCase),
            ListingType = GetListingType(pr.Status, pr.ListingType),
            Category = pr.Category,
            PostedAt = pr.PostedAt,
            PostedTimeAgo = GetTimeAgoText(pr.PostedAt),
            Title = pr.Title,
            DistanceKm = centre != null && pr.Location != null
                ? Math.Round(GetDistanceKm(pr.Location, centre), 2)
                : null,
            PreferredLocations = DeserializeJson<List<PreferredLocationDto>>(
                pr.PreferredLocationsJson) ?? new(),
            Budget = DeserializeJson<BudgetResponseDto>(pr.BudgetJson),
            RequiredArea = DeserializeJson<RequiredAreaDto>(pr.RequiredAreaJson),
            Urgency = DeserializeJson<UrgencyDto>(pr.UrgencyJson),
            ClientPreferences = DeserializeJson<List<ClientPreferenceDto>>(
                pr.ClientPreferencesJson) ?? new(),
            Actions = new ActionsDto
            {
                CanContact = true,
                ContactCreditsRequired = 5
            }
        };

        // Map posted by info from User
        if (pr.User != null)
        {
            responseItem.PostedBy = new PostedByDto
            {
                UserId = pr.UserId.ToString(),
                Name = pr.User.Name,
                Initials = GetInitials(pr.User.Name),
                Locality = pr.Locality,
                Role = "PropSeekr",
                AvatarUrl = pr.User.ProfilePhotoUrl
            };
        }

        return responseItem;
    }

    private T? DeserializeJson<T>(string json) where T : class
    {
        try
        {
            if (string.IsNullOrEmpty(json) || json == "{}" || json == "[]")
                return null;

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch
        {
            return null;
        }
    }

    private static string GetListingType(string status, string? listingType)
    {
        if (!string.IsNullOrWhiteSpace(listingType))
            return listingType.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(status))
            return string.Empty;

        return status.Trim().ToUpperInvariant() switch
        {
            "LOOKING" => "DEMAND",
            "ACTIVE" => "SUPPLY",
            _ => status.Trim().ToUpperInvariant()
        };
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

    private string GetTimeAgoText(DateTime postedAt)
    {
        var timeSpan = DateTime.UtcNow - postedAt;

        if (timeSpan.TotalMinutes < 1)
            return "Abhi dala";
        if (timeSpan.TotalHours < 1)
            return $"{(int)timeSpan.TotalMinutes}m pehle";
        if (timeSpan.TotalDays < 1)
            return $"{(int)timeSpan.TotalHours}h pehle";
        if (timeSpan.TotalDays == 1)
            return "Kal dala";
        if (timeSpan.TotalDays < 7)
            return $"{(int)timeSpan.TotalDays}d pehle";

        return postedAt.ToString("dd MMM");
    }

    private string GetInitials(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "";

        var parts = name.Split(' ');
        var initials = parts[0][0].ToString().ToUpper();
        if (parts.Length > 1)
            initials += parts[^1][0].ToString().ToUpper();

        return initials;
    }
}
