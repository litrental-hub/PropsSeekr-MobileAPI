using PropSeekr.Services;
using Xunit;

namespace PropSeekr.Tests;

public sealed class ContactRedactionTests
{
    [Theory]
    [InlineData("Call 9826-810081", "Call [contact hidden]")]
    [InlineData("WhatsApp +91 98765 43210", "WhatsApp [contact hidden]")]
    [InlineData("Mail broker@example.com", "Mail [contact hidden]")]
    public void Redact_HidesContactVariantsBeforeReveal(string source, string expected)
    {
        Assert.Equal(expected, ContactRedaction.Redact(source, contactRevealed: false));
    }

    [Fact]
    public void Redact_PreservesContactAfterReveal()
    {
        const string source = "Call 9876543210 or broker@example.com";
        Assert.Equal(source, ContactRedaction.Redact(source, contactRevealed: true));
    }
}
