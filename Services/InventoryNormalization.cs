using System.Text.RegularExpressions;

namespace PropSeekr.Services;

public static partial class InventoryNormalization
{
    public static string? PropertyType(string? value)
    {
        var token = Token(value);
        return token switch
        {
            "APARTMENT" or "FLAT" or "FLATAPARTMENT" or "PENTHOUSE" => "APARTMENT",
            "HOUSE" or "INDEPENDENTHOUSE" => "INDEPENDENT_HOUSE",
            "BUNGALOW" or "VILLA" or "BUNGALOWVILLA" => "BUNGALOW",
            "PLOT" or "LAND" or "PLOTLAND" => "PLOT",
            "AGRICULTURALLAND" or "FARMLAND" => "AGRICULTURAL_LAND",
            "OFFICE" or "OFFICESPACE" or "COMMERCIALOFFICE" or "COMMERCIALSPACE" => "OFFICE",
            "SHOP" or "RETAIL" or "SHOPRETAIL" or "SHOWROOM" => "SHOP",
            "WAREHOUSE" or "GODOWN" => "WAREHOUSE",
            "PG" or "HOSTEL" or "PGHOSTEL" => "PG",
            "INSTITUTION" or "INSTITUTIONSPECIALISED" or "INSTITUTIONSPECIALIZED" => "INSTITUTION",
            "ANY" => "ANY",
            _ => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant()
        };
    }

    public static string? Configuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = WhitespaceRegex().Replace(value.Trim().ToUpperInvariant(), string.Empty);
        return compact
            .Replace("BEDROOMS", "BHK", StringComparison.Ordinal)
            .Replace("BEDROOM", "BHK", StringComparison.Ordinal);
    }

    public static string[] Configurations(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(Configuration)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string? Furnishing(string? value)
    {
        var token = Token(value);
        return token switch
        {
            "BARE" or "UNFURNISHED" or "NONE" => "UNFURNISHED",
            "SEMI" or "SEMIFURNISHED" => "SEMI_FURNISHED",
            "FURNISHED" or "FULLYFURNISHED" => "FURNISHED",
            "ANY" or "NOPREFERENCE" => "ANY",
            _ => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant()
        };
    }

    public static string? Facing(string? value)
    {
        var token = Token(value);
        return token switch
        {
            "N" or "NORTH" => "NORTH",
            "S" or "SOUTH" => "SOUTH",
            "E" or "EAST" => "EAST",
            "W" or "WEST" => "WEST",
            "NE" or "NORTHEAST" => "NORTH_EAST",
            "NW" or "NORTHWEST" => "NORTH_WEST",
            "SE" or "SOUTHEAST" => "SOUTH_EAST",
            "SW" or "SOUTHWEST" => "SOUTH_WEST",
            "ANY" or "NOPREFERENCE" => "ANY",
            _ => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant()
        };
    }

    public static string BudgetType(string? value) => Token(value) switch
    {
        "FLEXIBLE" or "NEGOTIABLE" => "FLEXIBLE",
        "NOBUDGET" or "DISCUSS" or "OPEN" => "NOBUDGET",
        _ => "FIXED"
    };

    private static string Token(string? value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
