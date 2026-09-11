using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public sealed class ConnectionExpiryWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ConnectionExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Clamp(
            configuration.GetValue("Workers:ConnectionExpiryIntervalSeconds", 60), 10, 3600);
        var batchSize = Math.Clamp(
            configuration.GetValue("Workers:ConnectionExpiryBatchSize", 200), 1, 500);

        await RunBatchAsync(batchSize, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunBatchAsync(batchSize, stoppingToken);
        }
    }

    private async Task RunBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IUnlockService>();
            var expired = await service.ExpirePendingRequestsAsync(batchSize);
            if (expired > 0)
            {
                logger.LogInformation(
                    "Expired {ExpiredCount} connection requests without charging wallets.", expired);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Connection expiry worker batch failed.");
        }
    }
}
