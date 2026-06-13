using PropSeekr.DTOs.Profile;

namespace PropSeekr.Services.Interfaces;

public interface IProfileService
{
    Task<ProfileResponseDto> GetProfileAsync(Guid userId);
    Task<ProfileResponseDto> UpdateProfileAsync(Guid userId, UpdateProfileRequestDto request);
    Task<ProfileResponseDto> UploadPhotoAsync(Guid userId, IFormFile file);
}
