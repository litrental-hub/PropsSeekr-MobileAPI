using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.FileProcessing;
using propseekr_file_processor;

namespace PropSeekr.Controllers;

/// <summary>
/// REST facade for the existing file-processing Lambda. Endpoint request and
/// response payloads are deliberately passed through unchanged so consumers
/// can move from the Lambda URL without a business-rule migration.
/// </summary>
[ApiController]
[Route("api/v1/file-processor")]
[AllowAnonymous] // Protect through an internal network/API gateway policy before public deployment.
public sealed class FileProcessorController : ControllerBase
{
    private readonly ILogger<FileProcessorController> _logger;
    private readonly FileProcessorHost _processorHost;
    private readonly string _bucketName;

    public FileProcessorController(
        ILogger<FileProcessorController> logger,
        FileProcessorHost processorHost,
        IConfiguration configuration)
    {
        _logger = logger;
        _processorHost = processorHost;
        _bucketName = configuration["FileProcessor:S3BucketName"]
            ?? Environment.GetEnvironmentVariable("S3_BUCKET_NAME")
            ?? "propseekr-chat-files";
    }

    [HttpPost("process")]
    public Task<IActionResult> Process() => InvokeApiGatewayRouteAsync("/process");

    [HttpPost("embed")]
    public Task<IActionResult> Embed() => InvokeApiGatewayRouteAsync("/embed");

    [HttpPost("ingest")]
    public Task<IActionResult> Ingest() => InvokeApiGatewayRouteAsync("/ingest");

    [HttpGet("matches")]
    public Task<IActionResult> Matches() => InvokeApiGatewayRouteAsync("/matches");

    [HttpPost("listing")]
    public Task<IActionResult> SubmitListing() => InvokeApiGatewayRouteAsync("/listing");

    [HttpPost("upload")]
    public async Task<IActionResult> Upload()
    {
        var result = await InvokeApiGatewayRouteAsync("/upload");
        if (result is not ContentResult { StatusCode: 200, Content: not null } content)
            return result;

        // Bucket selection is infrastructure configuration, not a mobile-client
        // concern. The client receives only its presigned URL and unique key.
        using var responseJson = JsonDocument.Parse(content.Content);
        var publicResponse = new Dictionary<string, object?>();
        foreach (var property in responseJson.RootElement.EnumerateObject())
        {
            if (!property.NameEquals("bucket"))
                publicResponse[property.Name] = property.Value.Clone();
        }

        return new ContentResult
        {
            StatusCode = content.StatusCode,
            Content = JsonSerializer.Serialize(publicResponse),
            ContentType = content.ContentType
        };
    }

    // Completion callback after the client has successfully PUT the presigned URL.
    // The bucket always comes from server configuration; body: { "key": "..." }.
    [HttpPost("pipeline")]
    public async Task<IActionResult> RunPipeline([FromBody] S3PipelineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Key))
            return BadRequest(new { error = "Body must have key." });

        var s3Event = new
        {
            Records = new[]
            {
                new { s3 = new { bucket = new { name = _bucketName }, @object = new { key = request.Key } } }
            }
        };
        return await InvokeAsync(JsonSerializer.SerializeToElement(s3Event));
    }

    private async Task<IActionResult> InvokeApiGatewayRouteAsync(string lambdaPath)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        var request = new APIGatewayProxyRequest
        {
            Path = lambdaPath,
            HttpMethod = Request.Method,
            Body = body,
            Headers = Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString()),
            QueryStringParameters = Request.Query.ToDictionary(x => x.Key, x => x.Value.ToString())
        };
        return await InvokeAsync(JsonSerializer.SerializeToElement(request));
    }

    private async Task<IActionResult> InvokeAsync(JsonElement eventPayload)
    {
        try
        {
            var response = await _processorHost.Processor.FunctionHandler(
                eventPayload,
                new RestLambdaContext(_logger, HttpContext));

            foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>())
            {
                if (!name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    Response.Headers[name] = value;
            }

            return new ContentResult
            {
                StatusCode = response.StatusCode,
                Content = response.Body,
                ContentType = response.Headers != null && response.Headers.TryGetValue("Content-Type", out var contentType)
                    ? contentType
                    : "application/json"
            };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(ex, "File processor is not configured.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "File processor configuration is incomplete." });
        }
    }
}

public sealed class S3PipelineRequest
{
    public string Key { get; init; } = string.Empty;
}
