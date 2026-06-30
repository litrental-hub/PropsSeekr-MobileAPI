namespace PropSeekr.DTOs.Search;

public class BudgetResponseDto
{
    public long Min { get; set; }
    public long Max { get; set; }
    public string DisplayValue { get; set; } = string.Empty;
    public string Currency { get; set; } = "INR";
}
