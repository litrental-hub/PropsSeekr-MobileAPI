using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Search;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

/// <summary>
/// Location-aware marketplace search over the canonical listing and requirement data.
/// The service deliberately does not use the legacy PropertyRequests aggregate.
/// </summary>
public class SearchPropertyService : ISearchPropertyService
{
    private const string ListingCategorySql = """
        CASE
            WHEN UPPER(COALESCE(l.property_type, '')) IN
                ('OFFICE', 'OFFICE SPACE', 'SHOP', 'SHOWROOM', 'WAREHOUSE', 'COMMERCIAL') THEN 'COMMERCIAL'
            WHEN UPPER(COALESCE(l.property_type, '')) IN ('PLOT', 'LAND', 'PLOT/LAND') THEN 'PLOT'
            ELSE 'RESIDENTIAL'
        END
        """;

    private const string RequirementCategorySql = """
        CASE
            WHEN UPPER(COALESCE(r.property_type, '')) IN
                ('OFFICE', 'OFFICE SPACE', 'SHOP', 'SHOWROOM', 'WAREHOUSE', 'COMMERCIAL') THEN 'COMMERCIAL'
            WHEN UPPER(COALESCE(r.property_type, '')) IN ('PLOT', 'LAND', 'PLOT/LAND') THEN 'PLOT'
            ELSE 'RESIDENTIAL'
        END
        """;

    private readonly AppDbContext _dbContext;

    public SearchPropertyService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SearchPropertyResponseDto> SearchPropertiesAsync(
        SearchPropertyRequestDto request,
        Guid userId)
    {
        request.Validate();
        _ = userId; // Auth is enforced by the controller; marketplace search is not ownership-scoped.

        var connection = _dbContext.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
            await connection.OpenAsync();

        try
        {
            var availableCount = await CountListingsAsync(connection, request);
            var lookingCount = await CountRequirementsAsync(connection, request);
            var demandSelected = string.Equals(request.ListingType, "DEMAND", StringComparison.OrdinalIgnoreCase);

            var listings = demandSelected
                ? []
                : await ReadListingsAsync(connection, request);
            var requirements = demandSelected
                ? await ReadRequirementsAsync(connection, request)
                : [];

            return new SearchPropertyResponseDto
            {
                Status = "success",
                AvailableCount = availableCount,
                LookingCount = lookingCount,
                TotalCount = demandSelected ? lookingCount : availableCount,
                Page = request.Pagination.Page,
                Limit = request.Pagination.Limit,
                Results = listings,
                Requirements = requirements
            };
        }
        finally
        {
            if (!wasOpen)
                await connection.CloseAsync();
        }
    }

    private static async Task<int> CountListingsAsync(DbConnection connection, SearchPropertyRequestDto request)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM ({BuildListingQuery(request, countOnly: true)}) filtered;";
        AddSearchParameters(command, request, isSupply: true, includePagination: false);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountRequirementsAsync(DbConnection connection, SearchPropertyRequestDto request)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM ({BuildRequirementQuery(request, countOnly: true)}) filtered;";
        AddSearchParameters(command, request, isSupply: false, includePagination: false);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<List<PropertySearchResultItemDto>> ReadListingsAsync(
        DbConnection connection,
        SearchPropertyRequestDto request)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BuildListingQuery(request, countOnly: false);
        AddSearchParameters(command, request, isSupply: true, includePagination: true);

        var items = new List<PropertySearchResultItemDto>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var configuration = ReadString(reader, "configuration");
            var propertyType = ReadString(reader, "property_type");
            var projectName = ReadString(reader, "project_name");
            var locality = ReadString(reader, "locality");
            var city = ReadString(reader, "city");
            var distanceKm = ReadNullable<double>(reader, "distance_km");
            var floor = ReadNullable<int>(reader, "floor_number");
            var furnishing = ReadString(reader, "furnishing");
            var facing = ReadString(reader, "facing");

            var features = new List<FeatureItemDto>();
            AddFeature(features, "🪑", furnishing);
            AddFeature(features, "🏢", floor.HasValue ? FormatFloor(floor.Value) : null);
            AddFeature(features, "🧭", facing);

            items.Add(new PropertySearchResultItemDto
            {
                Id = Convert.ToString(reader["listingid"])!,
                ListingType = "SUPPLY",
                TransactionType = NormalizeResponseTransaction(ReadString(reader, "transaction_type")),
                Title = BuildListingTitle(configuration, propertyType, projectName),
                Subtitle = JoinNonBlank(" · ", locality, city),
                Category = ReadString(reader, "category"),
                PropertyType = propertyType,
                Bhk = configuration,
                Status = ReadString(reader, "status"),
                Price = ReadNullable<decimal>(reader, "price"),
                PriceUnit = ReadString(reader, "price_unit"),
                BuiltUpSize = ReadNullable<decimal>(reader, "built_up_size"),
                AvailableFrom = null,
                CreatedAt = ReadNullable<DateTime>(reader, "created_at"),
                LastRefreshedAt = ReadNullable<DateTime>(reader, "last_refreshed_at"),
                FreshnessCategory = ReadString(reader, "freshness_category"),
                UnlockCost = null,
                IsNearby = distanceKm.HasValue && distanceKm.Value <= request.Location.RadiusKm,
                DistanceKm = distanceKm,
                LocationLabel = BuildLocationLabel(distanceKm, locality, city),
                Locality = locality,
                City = city,
                Furnishing = furnishing,
                Facing = facing,
                FloorNumber = floor,
                ProjectName = projectName,
                RoadInfo = ReadString(reader, "road_info"),
                Features = features,
                Preferences = []
            });
        }

        return items;
    }

    private static async Task<List<RequirementSearchResultItemDto>> ReadRequirementsAsync(
        DbConnection connection,
        SearchPropertyRequestDto request)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BuildRequirementQuery(request, countOnly: false);
        AddSearchParameters(command, request, isSupply: false, includePagination: true);

        var items = new List<RequirementSearchResultItemDto>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var configurations = reader["configurations"] is string[] values ? values : [];
            var propertyType = ReadString(reader, "property_type");
            var locality = ReadString(reader, "locality");
            var city = ReadString(reader, "city");
            var distanceKm = ReadNullable<double>(reader, "distance_km");
            var budget = ReadNullable<decimal>(reader, "budget");

            items.Add(new RequirementSearchResultItemDto
            {
                Id = Convert.ToString(reader["requirementid"])!,
                ListingType = "DEMAND",
                TransactionType = NormalizeResponseTransaction(ReadString(reader, "transaction_type")),
                Title = BuildRequirementTitle(configurations, propertyType),
                Sub = JoinNonBlank(" · ", FormatMoney(budget, ReadString(reader, "budget_unit")), locality, city),
                PropertyType = propertyType,
                Configurations = configurations,
                Budget = budget,
                BudgetUnit = ReadString(reader, "budget_unit"),
                RequiredSize = ReadNullable<decimal>(reader, "required_size"),
                FurnishingPreference = ReadString(reader, "furnishing_pref"),
                FacingPreference = ReadString(reader, "facing_pref"),
                Status = ReadString(reader, "status"),
                Locality = locality,
                City = city,
                DistanceKm = distanceKm,
                CreatedAt = ReadNullable<DateTime>(reader, "created_at"),
                LastRefreshedAt = ReadNullable<DateTime>(reader, "last_refreshed_at"),
                FreshnessCategory = ReadString(reader, "freshness_category")
            });
        }

        return items;
    }

    private static string BuildListingQuery(SearchPropertyRequestDto request, bool countOnly)
    {
        var distanceSql = DistanceSql("ml");
        var select = countOnly
            ? "SELECT l.listingid"
            : $"""
                SELECT
                    l.listingid,
                    l.listing_type AS transaction_type,
                    l.property_type,
                    l.configuration,
                    l.price,
                    l.price_unit,
                    COALESCE(l.size, (
                        SELECT MAX(ls.size_sqft) FROM public.listing_sizes ls WHERE ls.listing_id = l.listingid
                    )) AS built_up_size,
                    l.furnishing,
                    l.facing,
                    l.floor_number,
                    l.status,
                    l.project_name,
                    l.road_info,
                    l.created_at,
                    l.last_refreshed_at,
                    l.freshness_category,
                    ml.area AS locality,
                    COALESCE(ml.city, l.city) AS city,
                    {distanceSql} AS distance_km,
                    {ListingCategorySql} AS category
                """;

        var sql = new StringBuilder($"""
            {select}
            FROM public.listings l
            INNER JOIN public.master ml ON ml.masterid = l.master_id
            WHERE ml.lat IS NOT NULL
              AND ml.lng IS NOT NULL
              AND COALESCE(NULLIF(ml.geocoding_status, ''), 'pending') IN ('resolved', 'verified')
              AND {distanceSql} <= @radius_km
              AND UPPER(COALESCE(l.listing_type, '')) = ANY(@transaction_types)
              AND UPPER(COALESCE(l.status, 'ACTIVE')) NOT IN
                  ('DELETED', 'CLOSED', 'SOLD', 'RENTED', 'INACTIVE', 'EXPIRED')
              AND l.isavailable
              AND (l.expires_at IS NULL OR l.expires_at > NOW())
            """);

        AddListingFilters(sql, request);

        if (!countOnly)
        {
            sql.AppendLine("ORDER BY distance_km ASC, COALESCE(l.last_refreshed_at, l.updated_at, l.created_at) DESC NULLS LAST");
            sql.AppendLine("LIMIT @limit OFFSET @offset");
        }

        return sql.ToString();
    }

    private static string BuildRequirementQuery(SearchPropertyRequestDto request, bool countOnly)
    {
        var distanceSql = DistanceSql("locality");
        var select = countOnly
            ? "SELECT r.requirementid"
            : $"""
                SELECT
                    r.requirementid,
                    r.requirement_type AS transaction_type,
                    r.property_type,
                    r.configurations,
                    r.budget,
                    r.budget_unit,
                    r.size AS required_size,
                    r.furnishing_pref,
                    r.facing_pref,
                    r.status,
                    r.created_at,
                    r.last_confirmed_at AS last_refreshed_at,
                    r.freshness_category,
                    nearest.area AS locality,
                    COALESCE(nearest.city, r.city) AS city,
                    nearest.distance_km,
                    {RequirementCategorySql} AS category
                """;

        var sql = new StringBuilder($"""
            {select}
            FROM public.requirements r
            INNER JOIN LATERAL (
                SELECT
                    locality.area,
                    locality.city,
                    {distanceSql} AS distance_km
                FROM unnest(r.preferred_locality_ids) AS locality_id
                INNER JOIN public.master locality ON locality.masterid = locality_id
                WHERE locality.lat IS NOT NULL AND locality.lng IS NOT NULL
                  AND COALESCE(NULLIF(locality.geocoding_status, ''), 'pending') IN ('resolved', 'verified')
                ORDER BY {distanceSql}
                LIMIT 1
            ) nearest ON TRUE
            WHERE nearest.distance_km <= @radius_km
              AND UPPER(COALESCE(r.requirement_type, '')) = ANY(@transaction_types)
              AND UPPER(COALESCE(r.status, 'ACTIVE')) NOT IN
                  ('DELETED', 'CLOSED', 'FULFILLED', 'INACTIVE', 'EXPIRED')
              AND r.isavailable
              AND (r.expires_at IS NULL OR r.expires_at > NOW())
            """);

        AddRequirementFilters(sql, request);

        if (!countOnly)
        {
            sql.AppendLine("ORDER BY nearest.distance_km ASC, COALESCE(r.last_confirmed_at, r.updated_at, r.created_at) DESC NULLS LAST");
            sql.AppendLine("LIMIT @limit OFFSET @offset");
        }

        return sql.ToString();
    }

    private static void AddListingFilters(StringBuilder sql, SearchPropertyRequestDto request)
    {
        if (GetCategories(request).Length > 0)
            sql.AppendLine($"AND {ListingCategorySql} = ANY(@categories)");
        if (NormalizeValues(request.Filters?.PropertyTypes).Length > 0)
            sql.AppendLine("AND UPPER(TRIM(COALESCE(l.property_type, ''))) = ANY(@property_types)");
        if (NormalizeConfigurations(request.Filters?.Configurations).Length > 0)
            sql.AppendLine("AND UPPER(REPLACE(TRIM(COALESCE(l.configuration, '')), ' ', '')) = ANY(@configurations)");

        var budget = GetBudget(request);
        if (budget.Min.HasValue)
            sql.AppendLine("AND l.price IS NOT NULL AND l.price >= @budget_min");
        if (budget.Max.HasValue)
            sql.AppendLine("AND l.price IS NOT NULL AND l.price <= @budget_max");
        if (!string.IsNullOrWhiteSpace(request.SearchQuery))
        {
            sql.AppendLine("""
                AND CONCAT_WS(' ', l.raw_message_text, l.property_type, l.configuration, l.project_name,
                    l.road_info, ml.area, ml.city, l.city) ILIKE @search_query
                """);
        }
    }

    private static void AddRequirementFilters(StringBuilder sql, SearchPropertyRequestDto request)
    {
        if (GetCategories(request).Length > 0)
            sql.AppendLine($"AND {RequirementCategorySql} = ANY(@categories)");
        if (NormalizeValues(request.Filters?.PropertyTypes).Length > 0)
            sql.AppendLine("AND UPPER(TRIM(COALESCE(r.property_type, ''))) = ANY(@property_types)");
        if (NormalizeConfigurations(request.Filters?.Configurations).Length > 0)
            sql.AppendLine("""
                AND EXISTS (
                    SELECT 1 FROM unnest(r.configurations) configuration
                    WHERE UPPER(REPLACE(TRIM(configuration), ' ', '')) = ANY(@configurations)
                )
                """);

        var budget = GetBudget(request);
        if (budget.Min.HasValue)
            sql.AppendLine("AND r.budget IS NOT NULL AND r.budget >= @budget_min");
        if (budget.Max.HasValue)
            sql.AppendLine("AND r.budget IS NOT NULL AND r.budget <= @budget_max");
        if (!string.IsNullOrWhiteSpace(request.SearchQuery))
        {
            sql.AppendLine("""
                AND CONCAT_WS(' ', r.raw_message_text, r.property_type, array_to_string(r.configurations, ' '),
                    nearest.area, nearest.city, r.city) ILIKE @search_query
                """);
        }
    }

    private static void AddSearchParameters(
        DbCommand command,
        SearchPropertyRequestDto request,
        bool isSupply,
        bool includePagination)
    {
        AddParameter(command, "@lat", request.Location.Lat);
        AddParameter(command, "@lng", request.Location.Lng);
        AddParameter(command, "@radius_km", request.Location.RadiusKm);
        AddParameter(command, "@transaction_types", GetTransactionTypes(request.TransactionType, isSupply));

        var categories = GetCategories(request);
        if (categories.Length > 0)
            AddParameter(command, "@categories", categories);

        var propertyTypes = NormalizeValues(request.Filters?.PropertyTypes);
        if (propertyTypes.Length > 0)
            AddParameter(command, "@property_types", propertyTypes);

        var configurations = NormalizeConfigurations(request.Filters?.Configurations);
        if (configurations.Length > 0)
            AddParameter(command, "@configurations", configurations);

        var budget = GetBudget(request);
        if (budget.Min.HasValue)
            AddParameter(command, "@budget_min", budget.Min.Value);
        if (budget.Max.HasValue)
            AddParameter(command, "@budget_max", budget.Max.Value);

        if (!string.IsNullOrWhiteSpace(request.SearchQuery))
            AddParameter(command, "@search_query", $"%{request.SearchQuery.Trim()}%");

        if (includePagination)
        {
            AddParameter(command, "@limit", request.Pagination.Limit);
            AddParameter(command, "@offset", (request.Pagination.Page - 1) * request.Pagination.Limit);
        }
    }

    internal static string[] GetTransactionTypes(string transactionType, bool isSupply)
    {
        var normalized = transactionType.Trim().ToUpperInvariant();
        if (normalized is "RENT" or "RENTAL")
            return ["RENT", "RENTAL"];

        return isSupply
            ? ["SELL", "SALE", "BUY_SELL"]
            : ["BUY", "PURCHASE", "BUY_SELL"];
    }

    internal static string NormalizeResponseTransaction(string? transactionType)
    {
        var normalized = transactionType?.Trim().ToUpperInvariant();
        return normalized is "RENT" or "RENTAL" ? "RENTAL" : "BUY_SELL";
    }

    internal static string BuildListingTitle(
        string? configuration,
        string? propertyType,
        string? projectName)
    {
        var structured = JoinNonBlank(" ", configuration, propertyType);
        if (!string.IsNullOrWhiteSpace(structured))
            return structured!;
        if (!string.IsNullOrWhiteSpace(projectName))
            return projectName.Trim();
        return "Property listing";
    }

    internal static string BuildRequirementTitle(
        IEnumerable<string>? configurations,
        string? propertyType)
    {
        var configuration = configurations?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var structured = JoinNonBlank(" ", configuration, propertyType);
        if (!string.IsNullOrWhiteSpace(structured))
            return structured!;
        return "Property requirement";
    }

    private static (long? Min, long? Max) GetBudget(SearchPropertyRequestDto request)
    {
        var filterBudget = request.Filters?.Budget;
        return (
            request.Budget?.Min ?? filterBudget?.Min,
            request.Budget?.Max ?? filterBudget?.Max);
    }

    private static string[] GetCategories(SearchPropertyRequestDto request)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Category) &&
            !string.Equals(request.Category, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            values.Add(request.Category);
        }

        if (request.Filters?.Categories is not null)
            values.AddRange(request.Filters.Categories);

        return NormalizeValues(values);
    }

    private static string[] NormalizeValues(IEnumerable<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .Distinct()
            .ToArray() ?? [];

    private static string[] NormalizeConfigurations(IEnumerable<string>? values) =>
        NormalizeValues(values)
            .Select(value => value.Replace(" ", string.Empty))
            .ToArray();

    private static string DistanceSql(string alias) => $"""
        6371.0088 * 2 * ASIN(SQRT(LEAST(1.0,
            POWER(SIN(RADIANS(({alias}.lat::double precision) - @lat) / 2), 2)
            + COS(RADIANS(@lat)) * COS(RADIANS({alias}.lat::double precision))
            * POWER(SIN(RADIANS(({alias}.lng::double precision) - @lng) / 2), 2)
        )))
        """;

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static T? ReadNullable<T>(DbDataReader reader, string name) where T : struct
    {
        var value = reader[name];
        if (value is null or DBNull)
            return null;
        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static string? ReadString(DbDataReader reader, string name)
    {
        var value = reader[name];
        return value is null or DBNull ? null : Convert.ToString(value)?.Trim();
    }

    private static void AddFeature(List<FeatureItemDto> features, string icon, string? label)
    {
        if (!string.IsNullOrWhiteSpace(label))
            features.Add(new FeatureItemDto { Icon = icon, Label = label });
    }

    private static string FormatFloor(int floor) => floor switch
    {
        0 => "Ground floor",
        1 => "1st floor",
        2 => "2nd floor",
        3 => "3rd floor",
        _ => $"{floor}th floor"
    };

    private static string? BuildLocationLabel(double? distanceKm, string? locality, string? city)
    {
        var distance = distanceKm.HasValue ? $"{distanceKm.Value:0.0} km" : null;
        return JoinNonBlank(" · ", distance, locality, city);
    }

    private static string? FormatMoney(decimal? value, string? unit)
    {
        if (!value.HasValue)
            return null;
        return JoinNonBlank(" ", $"₹{value.Value:N0}", unit);
    }

    private static string? JoinNonBlank(string separator, params string?[] values)
    {
        var present = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim());
        var result = string.Join(separator, present);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

}
