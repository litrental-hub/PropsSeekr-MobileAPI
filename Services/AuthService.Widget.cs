using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PropSeekr.DTOs.Auth;
using PropSeekr.Models;

namespace PropSeekr.Services;

public partial class AuthService
{
    private bool WidgetEnabled => _configuration.GetValue<bool>("Msg91:WidgetEnabled");

    private void EnsureWidgetSupported(SendOtpRequestDto request)
    {
        if (WidgetEnabled && !request.SupportsWidget)
            throw new InvalidOperationException("Please update PropSeekr to use mobile verification.");
    }

    private static string NormalizeIndianWidgetMobile(string value)
    {
        var mobile = value.Trim();
        if (!Regex.IsMatch(mobile, @"^(?:\+?91)?[6-9][0-9]{9}$"))
            throw new InvalidOperationException("Enter a valid Indian mobile number.");
        return mobile[^10..];
    }

    private async Task<OtpResponseDto> CreateWidgetSessionAsync(string mobile, User? user, PendingRegistration? pending)
    {
        var widgetId = _configuration["Msg91:WidgetId"];
        var clientToken = _configuration["Msg91:WidgetTokenAuth"];
        if (string.IsNullOrWhiteSpace(widgetId) || string.IsNullOrWhiteSpace(clientToken) ||
            string.IsNullOrWhiteSpace(_configuration["Msg91:AuthKey"]) || _widgetVerifier is null)
            throw new InvalidOperationException("Mobile verification is not configured.");

        // DB-backed per-number admission control shared by all API instances.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();
        var lockKey = "widget:" + mobile;
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({lockKey}))");
        var now = DateTime.UtcNow;
        if (await _dbContext.WidgetOtpChallenges.CountAsync(x => x.MobileNumber == mobile && x.CreatedAt > now.AddMinutes(-15)) >= 5)
            throw new InvalidOperationException("Too many mobile verification requests. Please try again later.");

        var challenge = new WidgetOtpChallenge
        {
            MobileNumber = mobile, UserId = user?.Id,
            PendingRegistrationId = user is null ? pending!.Id : null,
            CreatedAt = now, ExpiresAt = now.AddMinutes(15)
        };
        _dbContext.WidgetOtpChallenges.Add(challenge);
        await _dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new OtpResponseDto
        {
            Message = "Open the verification widget to verify your mobile number.",
            ExpiryMinutes = 15, ExpiresAt = challenge.ExpiresAt,
            Widget = new WidgetOtpSessionDto
            {
                ChallengeId = challenge.Id, WidgetId = widgetId, TokenAuth = clientToken,
                Identifier = "91" + mobile, ExpiresAt = challenge.ExpiresAt
            }
        };
    }

    public async Task<VerifyOtpResponseDto> VerifyWidgetOtpAsync(VerifyWidgetOtpRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!WidgetEnabled || _widgetVerifier is null)
            throw new InvalidOperationException("Mobile widget verification is unavailable.");
        if (request.ChallengeId == Guid.Empty || string.IsNullOrWhiteSpace(request.AccessToken) || request.AccessToken.Length > 8192)
            throw new InvalidOperationException("Invalid mobile verification result.");
        var challenge = await _dbContext.WidgetOtpChallenges.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.ChallengeId && x.ConsumedTokenHash == null && x.ExpiresAt > DateTime.UtcNow, cancellationToken);
        if (challenge is null)
            throw new InvalidOperationException("Mobile verification has expired or was already used. Please start again.");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.AccessToken)));
        if (await _dbContext.WidgetOtpChallenges.AnyAsync(x => x.ConsumedTokenHash == hash, cancellationToken))
            throw new InvalidOperationException("Mobile verification was already used. Please start again.");
        await _widgetVerifier.VerifyAsync(request.AccessToken, "91" + challenge.MobileNumber,
            challenge.CreatedAt, cancellationToken);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var claimed = await _dbContext.WidgetOtpChallenges
                .Where(x => x.Id == challenge.Id && x.ConsumedTokenHash == null && x.ExpiresAt > DateTime.UtcNow)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedTokenHash, hash), cancellationToken);
            if (claimed != 1)
                throw new InvalidOperationException("Mobile verification has expired or was already used. Please start again.");
            var result = await CompleteMobileVerificationAsync(challenge.MobileNumber, challenge.CreatedAt, challenge);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } ||
                                   ex is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } })
        {
            throw new InvalidOperationException("Mobile verification was already completed. Please start again.");
        }
    }
}
