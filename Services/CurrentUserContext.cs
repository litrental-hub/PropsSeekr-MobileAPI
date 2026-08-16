using System.Security.Claims;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public sealed class CurrentUserContext(IHttpContextAccessor httpContextAccessor) : ICurrentUserContext
{
    public bool TryGetLocalUserId(out Guid userId)
    {
        var claim = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out userId);
    }
}
