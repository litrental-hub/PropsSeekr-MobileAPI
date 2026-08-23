namespace PropSeekr.Services.Interfaces;

/// <summary>Resolves an authenticated account to the broker record that owns matches and credits.</summary>
public interface IBrokerIdentityService
{
    Task<int?> GetBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<int> GetOrCreateBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default);
}
