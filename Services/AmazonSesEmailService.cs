using Amazon;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public class AmazonSesEmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AmazonSesEmailService> _logger;

    public AmazonSesEmailService(
        IConfiguration configuration,
        ILogger<AmazonSesEmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendEmailAsync(
        string recipientEmail,
        string subject,
        string htmlBody,
        string textBody,
        CancellationToken cancellationToken = default)
    {
        var fromAddress = _configuration["Email:FromAddress"] ?? "no-reply@propseekr.com";
        var fromName = _configuration["Email:FromName"] ?? "Propseek";
        var senderSource = $"{fromName} <{fromAddress}>";

        var regionName = _configuration["AWS:Region"] ?? _configuration["AWS:DefaultRegion"] ?? "ap-south-1";
        var region = RegionEndpoint.GetBySystemName(regionName);

        // Uses the AWS SDK default credential chain: IAM roles in AWS and an AWS profile,
        // environment credentials, or .NET user secrets for local development.
        IAmazonSimpleEmailService client = new AmazonSimpleEmailServiceClient(region);

        using (client)
        {
            var sendRequest = new SendEmailRequest
            {
                Source = senderSource,
                Destination = new Destination
                {
                    ToAddresses = new List<string> { recipientEmail }
                },
                Message = new Message
                {
                    Subject = new Content(subject),
                    Body = new Body
                    {
                        Html = new Content
                        {
                            Charset = "UTF-8",
                            Data = htmlBody
                        },
                        Text = new Content
                        {
                            Charset = "UTF-8",
                            Data = textBody
                        }
                    }
                }
            };

            try
            {
                var response = await client.SendEmailAsync(sendRequest, cancellationToken);
                _logger.LogInformation(
                    "Amazon SES email successfully sent to {Recipient}. MessageId: {MessageId}",
                    recipientEmail,
                    response.MessageId);
            }
            catch (AmazonSimpleEmailServiceException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Amazon SES Error sending email to {Recipient}. AWS ErrorCode: {ErrorCode}, Message: {Message}",
                    recipientEmail,
                    ex.ErrorCode,
                    ex.Message);

                // For local development or sandbox fallback, do not crash if AWS SES is unconfigured
                if (ex.ErrorCode == "InvalidAccessKeyId" || ex.ErrorCode == "UnrecognizedClientException" || ex.ErrorCode == "MessageRejected")
                {
                    _logger.LogInformation("AWS SES is unconfigured or in Sandbox. Email fallback logged for {Recipient}", recipientEmail);
                    return;
                }

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deliver transactional email to {Recipient} via Amazon SES.", recipientEmail);
                // Graceful fallback for local development without AWS credentials
                _logger.LogInformation("Local Email Fallback for {Recipient}: Subject='{Subject}'", recipientEmail, subject);
            }
        }
    }
}
