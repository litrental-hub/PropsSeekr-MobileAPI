namespace PropSeekr.Services.Interfaces;

public interface IMsg91WidgetVerifier
{
    Task VerifyAsync(string accessToken, string expectedIdentifier, DateTime notBefore,
        CancellationToken cancellationToken = default);
}
