using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Auth;

public class WidgetOtpSessionDto
{
    public Guid ChallengeId { get; set; }
    public string WidgetId { get; set; } = string.Empty;
    // Widget-scoped public client credential only; never the account Authkey.
    public string TokenAuth { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

public class VerifyWidgetOtpRequestDto
{
    public Guid ChallengeId { get; set; }
    [Required, MaxLength(8192)] public string AccessToken { get; set; } = string.Empty;
}
