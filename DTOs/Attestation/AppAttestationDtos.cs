using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Attestation;

public class CreateAttestationChallengeRequestDto
{
    [Required, RegularExpression("^(android|ios)$", ErrorMessage = "Platform must be android or ios.")]
    public string Platform { get; set; } = string.Empty;
    [Required, RegularExpression("^(PaymentOrder|PaymentVerify|PropertyUnlock|AppAttestEnroll)$")]
    public string Purpose { get; set; } = string.Empty;
}

public class CreateAttestationChallengeResponseDto
{
    public Guid ChallengeId { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

public class EnrollAppAttestRequestDto
{
    [Required] public Guid ChallengeId { get; set; }
    [Required, MaxLength(255)] public string KeyId { get; set; } = string.Empty;
    [Required] public string AttestationObject { get; set; } = string.Empty;
    [MaxLength(50)] public string? AppVersion { get; set; }
}

public class VerifyPlayIntegrityRequestDto
{
    [Required] public Guid ChallengeId { get; set; }
    [Required, MinLength(20)] public string IntegrityToken { get; set; } = string.Empty;
    [Required, RegularExpression("^[A-Za-z0-9_-]{43}$")] public string RequestHash { get; set; } = string.Empty;
}

public class VerifyAppAttestAssertionRequestDto
{
    [Required] public Guid ChallengeId { get; set; }
    [Required, MaxLength(255)] public string KeyId { get; set; } = string.Empty;
    [Required] public string Assertion { get; set; } = string.Empty;
    [Required, RegularExpression("^[A-Za-z0-9_-]{43}$")] public string RequestHash { get; set; } = string.Empty;
}

public class AppAttestationDecisionDto
{
    public string Decision { get; set; } = "REJECT";
    public Guid ChallengeId { get; set; }
}
