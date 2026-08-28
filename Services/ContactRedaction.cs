using System.Text.RegularExpressions;

namespace PropSeekr.Services;

internal static class ContactRedaction
{
    private static readonly Regex EmailPattern = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex IndianPhonePattern = new(
        @"(?<!\d)(?:\+?91[\s.-]?)?[6-9]\d(?:[\s.-]?\d){8}(?!\d)",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public static string Redact(string? value, bool contactRevealed)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (contactRevealed) return value;

        var redacted = EmailPattern.Replace(value, "[contact hidden]");
        return IndianPhonePattern.Replace(redacted, "[contact hidden]");
    }
}
