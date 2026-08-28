using PropSeekr.Services;
using Xunit;

namespace PropSeekr.Tests;

public sealed class BrokerNotificationPresentationTests
{
    [Fact]
    public void RequestingBroker_SeesAcceptedAndUnlockedOutcome()
    {
        var presentation = BrokerNotificationPresentationResolver.Resolve("confirm_accepted", "accepted");

        Assert.Equal("BROKER_ACCEPTED", presentation.ApiType);
        Assert.Contains("Accepted", presentation.Title);
        Assert.Contains("Contact Unlocked", presentation.Title);
        Assert.Contains("other broker accepted", presentation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReceivingBroker_RequestCardChangesWhenRequestIsAccepted()
    {
        var presentation = BrokerNotificationPresentationResolver.Resolve("confirm_pending", "accepted");

        Assert.Equal("BROKER_ACCEPTED", presentation.ApiType);
        Assert.Contains("Accepted", presentation.Title);
        Assert.DoesNotContain("review and accept", presentation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PendingRequestStillPromptsTheReceivingBrokerToRespond()
    {
        var presentation = BrokerNotificationPresentationResolver.Resolve("confirm_pending", "pending");

        Assert.Equal("BROKER_UNLOCK", presentation.ApiType);
        Assert.Contains("review and accept", presentation.Message, StringComparison.OrdinalIgnoreCase);
    }
}
