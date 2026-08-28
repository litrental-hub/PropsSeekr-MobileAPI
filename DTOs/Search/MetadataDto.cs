namespace PropSeekr.DTOs.Search;

public class MetadataDto
{
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int Limit { get; set; }
    public bool IsAdminView { get; set; }
}
