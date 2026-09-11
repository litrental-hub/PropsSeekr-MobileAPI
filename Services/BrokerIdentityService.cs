using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

/// <summary>Claims an imported broker only after the account has verified its mobile number.</summary>
public sealed class BrokerIdentityService : IBrokerIdentityService
{
    private readonly AppDbContext _db;
    private readonly IWalletAccountingService _walletAccounting;
    public BrokerIdentityService(AppDbContext db, IWalletAccountingService walletAccounting)
    {
        _db = db;
        _walletAccounting = walletAccounting;
    }

    public async Task<int?> GetBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => x.BrokerId)
            .SingleOrDefaultAsync(cancellationToken);
        return user;
    }

    public async Task<int> GetOrCreateBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.SingleAsync(x => x.Id == userId, cancellationToken);
        if (!user.IsMobileVerified)
            throw new InvalidOperationException("Verify the mobile number before claiming a broker profile.");

        var existing = user.BrokerId;
        if (existing.HasValue)
        {
            await EnsureWalletAsync(existing.Value, cancellationToken);
            return existing.Value;
        }

        var mobile = Digits(user.MobileNumber ?? string.Empty);
        if (mobile.Length != 10)
            throw new InvalidOperationException("A valid 10-digit mobile number is required for a broker profile.");

        var imported = await _db.Database.SqlQueryRaw<BrokerIdRow>(
            "SELECT brokerid AS \"BrokerId\" FROM brokers WHERE right(regexp_replace(phone_number, '\\D', '', 'g'), 10) = {0} LIMIT 1",
            mobile).SingleOrDefaultAsync(cancellationToken);

        if (imported is not null)
        {
            user.BrokerId = imported.BrokerId;
            await _db.SaveChangesAsync(cancellationToken);
            await EnsureWalletAsync(imported.BrokerId, cancellationToken);
            return imported.BrokerId;
        }

        var brokerageName = user.ReraRegistrationNumber ?? "Independent Broker";
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO brokers (
                name,
                phone_number,
                brokerage_name,
                confirmation_compliance_rate,
                visibility_penalty_flag)
            VALUES ({user.Name}, {mobile}, {brokerageName}, 100.00, FALSE)
            """, cancellationToken);
        var brokerId = await _db.Database.SqlQueryRaw<BrokerIdRow>(
            "SELECT brokerid AS \"BrokerId\" FROM brokers WHERE phone_number = {0}", mobile)
            .SingleAsync(cancellationToken);
        user.BrokerId = brokerId.BrokerId;
        await _db.SaveChangesAsync(cancellationToken);
        await EnsureWalletAsync(brokerId.BrokerId, cancellationToken);
        return brokerId.BrokerId;
    }

    private Task EnsureWalletAsync(int brokerId, CancellationToken cancellationToken) =>
        _walletAccounting.EnsureCurrentPeriodWalletAsync(brokerId, DateTime.UtcNow, cancellationToken);

    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());
    private sealed class BrokerIdRow { public int BrokerId { get; set; } }
}
