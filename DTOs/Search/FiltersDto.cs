namespace PropSeekr.DTOs.Search;

public class FiltersDto
{
    public List<string> PropertyTypes { get; set; } = new();
    public List<string> Configurations { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public BudgetFilterDto Budget { get; set; } = new();
}
