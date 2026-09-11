namespace PropSeekr.Services.Interfaces;

public interface IListingMediaStorage
{
    Task<string> SaveAsync(int listingId, IFormFile file, string extension, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken = default);
    Task DeleteAsync(string storagePath, CancellationToken cancellationToken = default);
}
