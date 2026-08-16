namespace PropSeekr.Services.Interfaces;

public interface ICurrentUserContext
{
    bool TryGetLocalUserId(out Guid userId);
}
