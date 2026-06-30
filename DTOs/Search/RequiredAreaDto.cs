namespace PropSeekr.DTOs.Search;

public class RequiredAreaDto
{
    public int Min { get; set; }
    public int Max { get; set; }
    public string DisplayValue { get; set; } = string.Empty;
    public string Unit { get; set; } = "SQFT";
}
