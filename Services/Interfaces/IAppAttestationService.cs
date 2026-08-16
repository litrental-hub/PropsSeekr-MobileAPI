using PropSeekr.DTOs.Attestation;

namespace PropSeekr.Services.Interfaces;

public interface IAppAttestationService
{
    Task<CreateAttestationChallengeResponseDto> CreateChallengeAsync(Guid userId, CreateAttestationChallengeRequestDto request, CancellationToken cancellationToken);
    Task EnrollAppleAppAttestAsync(Guid userId, EnrollAppAttestRequestDto request, CancellationToken cancellationToken);
    Task<AppAttestationDecisionDto> VerifyPlayIntegrityAsync(Guid userId, VerifyPlayIntegrityRequestDto request, CancellationToken cancellationToken);
    Task<AppAttestationDecisionDto> VerifyAppleAssertionAsync(Guid userId, VerifyAppAttestAssertionRequestDto request, CancellationToken cancellationToken);
    Task<bool> ConsumeVerifiedRequestAsync(HttpContext context, Guid userId, string purpose, CancellationToken cancellationToken);
    Task<AppAttestationVerificationResult> VerifySensitiveRequestAsync(HttpContext context, Guid userId, string purpose, CancellationToken cancellationToken);
}

public sealed record AppAttestationVerificationResult(bool Succeeded, string Platform, string Reason);
