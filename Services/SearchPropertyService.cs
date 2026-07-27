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
                    p.Location.Distance(centre) <= radiusMetres);
            }

            // Filter by Category
            if (!string.IsNullOrWhiteSpace(request.Category))
            {
                query = query.Where(p => p.Category.ToLower() == request.Category.ToLower());
            }

            // Filter by budget
            if (request.Budget != null)
            {
                query = FilterByBudget(query, request.Budget);
            }

            // Filter by search query (title and user info)
            if (!string.IsNullOrWhiteSpace(request.SearchQuery))
            {
                var searchQuery = request.SearchQuery.ToLower();
                query = query.Where(p =>
                    p.Title.ToLower().Contains(searchQuery) ||
                    (p.User != null && p.User.Name.ToLower().Contains(searchQuery)));
            }

            // Apply custom filters if present
            if (request.Filters != null)
            {
                query = ApplyCustomFilters(query, request.Filters);
            }

            // Calculate pagination
            var pageNumber = request.Pagination.Page > 0 ? request.Pagination.Page : 1;
            var limit = request.Pagination.Limit > 0 ? request.Pagination.Limit : 10;
            var skip = (pageNumber - 1) * limit;

            // Total count from database query
            var totalCount = await query.CountAsync();

            // Production Database Sorting & Pagination (Skip/Take executed before ToListAsync)
            if (centre != null)
            {
                query = query.OrderBy(p => p.Location != null ? p.Location.Distance(centre) : double.MaxValue);
            }
            else
            {
                query = query.OrderByDescending(p => p.PostedAt);
            }

            var propertyRequests = await query
                .Include(p => p.User)
                .Skip(skip)
                .Take(limit)
                .ToListAsync();

            // Map to response DTOs with calculated distance
            var responseItems = propertyRequests.Select(pr => MapToResponseDto(pr, centre)).ToList();

            return new SearchPropertyResponseDto
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
            PostedBy = pr.User != null ? new PostedByDto
            {
                UserId = pr.User.Id.ToString(),
                Name = pr.User.Name,
                Initials = GetInitials(pr.User.Name),
                Locality = pr.Locality,
                Role = "PropSeekr",
                AvatarUrl = pr.User.ProfilePhotoUrl
            } : null,
            Actions = new ActionsDto
            {
                CanContact = true,
                ContactCreditsRequired = 1
            }
        };

        return responseItem;
    }

    private static string GetListingType(string? status, string? listingType)
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

    private string GetTimeAgoText(DateTime postedAt)
    {
        var timeSpan = DateTime.UtcNow - postedAt;

        if (timeSpan.TotalMinutes < 1)
            return "Just now";
        if (timeSpan.TotalMinutes < 60)
            return $"{(int)timeSpan.TotalMinutes} mins ago";
        if (timeSpan.TotalHours < 24)
            return $"{(int)timeSpan.TotalHours} hours ago";
        if (timeSpan.TotalDays < 30)
            return $"{(int)timeSpan.TotalDays} days ago";

        return postedAt.ToString("MMM dd, yyyy");
    }

    private T? DeserializeJson<T>(string json) where T : class
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
