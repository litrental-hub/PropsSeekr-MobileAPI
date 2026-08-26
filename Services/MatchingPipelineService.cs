using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.Runtime;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

/// <summary>Starts the shared embedding + stored-procedure pipeline for one new UI record.</summary>
public sealed class MatchingPipelineService : IMatchingPipelineService, IDisposable
{
    private readonly IAmazonLambda _lambda;
    private readonly string _functionName;

    public MatchingPipelineService(IConfiguration configuration)
    {
        _functionName = configuration["MatchingPipeline:FunctionName"] ?? "propseekr-file-processor";
        var region = RegionEndpoint.GetBySystemName(configuration["AWS:Region"] ?? "ap-south-1");
        var accessKey = configuration["AWS:AccessKeyId"];
        var secretKey = configuration["AWS:SecretAccessKey"];
        _lambda = string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey)
            ? new AmazonLambdaClient(region)
            : new AmazonLambdaClient(new BasicAWSCredentials(accessKey, secretKey), region);
    }

    public Task TriggerForListingAsync(int listingId, CancellationToken cancellationToken = default) =>
        TriggerAsync(new { listing_id = listingId }, cancellationToken);

    public Task TriggerForRequirementAsync(int requirementId, CancellationToken cancellationToken = default) =>
        TriggerAsync(new { requirement_id = requirementId }, cancellationToken);

    private async Task TriggerAsync(object body, CancellationToken cancellationToken)
    {
        var eventPayload = JsonSerializer.Serialize(new
        {
            path = "/embed",
            httpMethod = "POST",
            isBase64Encoded = false,
            headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            body = JsonSerializer.Serialize(body)
        });

        var response = await _lambda.InvokeAsync(new InvokeRequest
        {
            FunctionName = _functionName,
            InvocationType = InvocationType.Event,
            Payload = eventPayload
        }, cancellationToken);

        if (response.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"Matching pipeline invocation returned HTTP {response.StatusCode}.");
    }

    public void Dispose() => _lambda.Dispose();
}
