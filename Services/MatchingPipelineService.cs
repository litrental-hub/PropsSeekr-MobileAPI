using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using PropSeekr.FileProcessing;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

/// <summary>Starts the shared embedding + stored-procedure pipeline for one new UI record.</summary>
public sealed class MatchingPipelineService : IMatchingPipelineService
{
    private readonly FileProcessorHost _processorHost;
    private readonly ILogger<MatchingPipelineService> _logger;

    public MatchingPipelineService(
        FileProcessorHost processorHost,
        ILogger<MatchingPipelineService> logger)
    {
        _processorHost = processorHost;
        _logger = logger;
    }

    public Task TriggerForListingAsync(int listingId, CancellationToken cancellationToken = default) =>
        TriggerAsync(new { listing_id = listingId }, cancellationToken);

    public Task TriggerForRequirementAsync(int requirementId, CancellationToken cancellationToken = default) =>
        TriggerAsync(new { requirement_id = requirementId }, cancellationToken);

    private async Task TriggerAsync(object body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var eventPayload = JsonSerializer.SerializeToElement(new
        {
            path = "/embed",
            httpMethod = "POST",
            isBase64Encoded = false,
            headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            body = JsonSerializer.Serialize(body)
        });

        var response = await _processorHost.Processor.FunctionHandler(
            eventPayload,
            new RestLambdaContext(_logger, Guid.NewGuid().ToString("N")));

        if (response.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"Matching pipeline returned HTTP {response.StatusCode}.");
    }
}
