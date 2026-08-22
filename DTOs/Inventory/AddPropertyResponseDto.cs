using System;

namespace PropSeekr.DTOs.Inventory;

public class AddPropertyResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public AddPropertyDataDto Data { get; set; } = new();
}

public class AddPropertyDataDto
{
    public string Id { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Price { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Views { get; set; } = 0;
    public int Matches { get; set; } = 0;
    public DateTime CreatedAt { get; set; }
}
