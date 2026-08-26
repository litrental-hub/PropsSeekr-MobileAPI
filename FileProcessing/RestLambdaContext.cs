using Amazon.Lambda.Core;
using Microsoft.Extensions.Logging;

namespace PropSeekr.FileProcessing;

internal sealed class RestLambdaContext : ILambdaContext
{
    public RestLambdaContext(ILogger logger, HttpContext httpContext)
    {
        Logger = new AspNetLambdaLogger(logger);
        AwsRequestId = httpContext.TraceIdentifier;
        FunctionName = "mobile-api-file-processor";
    }

    public string AwsRequestId { get; }
    public IClientContext? ClientContext => null;
    public string FunctionName { get; }
    public string FunctionVersion => "REST";
    public ICognitoIdentity? Identity => null;
    public string InvokedFunctionArn => "rest-api";
    public ILambdaLogger Logger { get; }
    public string LogGroupName => "mobile-api";
    public string LogStreamName => "file-processor";
    public int MemoryLimitInMB => 0;
    public TimeSpan RemainingTime => TimeSpan.FromMinutes(15);
}

internal sealed class AspNetLambdaLogger : ILambdaLogger
{
    private readonly ILogger _logger;

    public AspNetLambdaLogger(ILogger logger) => _logger = logger;

    public void Log(string message) => _logger.LogInformation("{Message}", message);
    public void LogLine(string message) => _logger.LogInformation("{Message}", message);
    public void LogInformation(string message) => _logger.LogInformation("{Message}", message);
    public void LogWarning(string message) => _logger.LogWarning("{Message}", message);
    public void LogError(string message) => _logger.LogError("{Message}", message);
    public void LogCritical(string message) => _logger.LogCritical("{Message}", message);
}
