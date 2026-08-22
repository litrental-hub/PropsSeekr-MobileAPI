namespace PropSeekr.DTOs.Matches;

public class RegisterBrokerRequestDto
{
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Locality { get; set; }
    public string? BrokerageName { get; set; }
}
