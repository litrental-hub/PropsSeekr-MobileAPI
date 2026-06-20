using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Auth;

public class AdminLoginRequestDto
{
    [Required]
    [JsonPropertyName("username")]
    public string UserName { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}
