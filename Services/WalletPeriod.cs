namespace PropSeekr.Services;

internal static class WalletPeriod
{
    // India has observed UTC+05:30 without daylight-saving changes since 1945.
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);

    public static (string Key, DateTime StartUtc, DateTime NextStartUtc) For(DateTime instant)
    {
        var utc = instant.Kind == DateTimeKind.Utc ? instant : instant.ToUniversalTime();
        var india = utc.Add(IndiaOffset);
        var localStart = new DateTime(india.Year, india.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var startUtc = DateTime.SpecifyKind(localStart.Subtract(IndiaOffset), DateTimeKind.Utc);
        var nextStartUtc = DateTime.SpecifyKind(localStart.AddMonths(1).Subtract(IndiaOffset), DateTimeKind.Utc);
        return ($"{india:yyyy-MM}", startUtc, nextStartUtc);
    }
}
