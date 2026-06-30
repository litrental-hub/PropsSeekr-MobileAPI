namespace PropSeekr.DTOs.Search;

public class LocationDto
{
    public string City { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double RadiusKm { get; set; }
}
