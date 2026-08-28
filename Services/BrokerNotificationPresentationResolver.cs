namespace PropSeekr.Services;

internal sealed record BrokerNotificationPresentation(string ApiType, string Title, string Message);

internal static class BrokerNotificationPresentationResolver
{
    internal static BrokerNotificationPresentation Resolve(string type, string? actionStatus)
    {
        var normalizedStatus = actionStatus?.Trim().ToLowerInvariant();

        if (type == "confirm_pending")
        {
            return normalizedStatus switch
            {
                "accepted" => new(
                    "BROKER_ACCEPTED",
                    "Connection Accepted — Contact Unlocked",
                    "You accepted this request. Contact details are now available to both brokers."),
                "rejected" => new(
                    "BROKER_REJECTED",
                    "Connection Request Declined",
                    "You declined this connection request. No tokens were deducted."),
                "expired" => new(
                    "BROKER_REQUEST",
                    "Unlock Request Expired",
                    "This connection request expired before both brokers confirmed."),
                "credit_required" => new(
                    "BROKER_REQUEST",
                    "Tokens Required to Connect",
                    "Contact remains protected until both brokers have a token and the reveal succeeds."),
                _ => new(
                    "BROKER_UNLOCK",
                    "Match Unlock Request",
                    "Another broker wants to connect. Open the match to review and accept.")
            };
        }

        return type switch
        {
            "match_found" => new(
                "MATCH",
                "New Property Match",
                "A new property match is available. Open Matches to review it."),
            "confirm_accepted" => new(
                "BROKER_ACCEPTED",
                "Request Accepted — Contact Unlocked",
                "The other broker accepted your request. Contact details are now available in this match."),
            "confirm_rejected" => new(
                "BROKER_REJECTED",
                "Connection Request Declined",
                "The other broker declined this connection request. No tokens were deducted."),
            "confirm_expired_resend" => new(
                "BROKER_REQUEST",
                "Confirmation Window Expired",
                "The previous confirmation expired. Open the match to confirm again."),
            "confirm_expired_counterparty" => new(
                "BROKER_REQUEST",
                "Unlock Request Expired",
                "The other broker did not confirm within the four-hour window."),
            _ => new(
                "SYSTEM",
                "PropSeekr Update",
                "Open PropSeekr to view this update.")
        };
    }
}
