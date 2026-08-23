using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

/// <summary>Uses normalized mobile numbers to bridge account and legacy broker data.</summary>
public sealed class BrokerIdentityService : IBrokerIdentityService
{
    private readonly AppDbContext _db;
    public BrokerIdentityService(AppDbContext db) => _db = db;

    public async Task<int?> GetBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null) return null;
        if (user.BrokerId.HasValue) return user.BrokerId.Value;
        var mobile = Digits(user.MobileNumber);
        if (mobile.Length < 10) return null;
        var row = await _db.Database.SqlQueryRaw<BrokerIdRow>(
            "SELECT brokerid AS \"BrokerId\" FROM brokers WHERE right(regexp_replace(phone_number, '\\D', '', 'g'), 10) = {0} LIMIT 1",
            mobile[^10..]).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var trackedUser = await _db.Users.SingleAsync(x => x.Id == userId, cancellationToken);
        trackedUser.BrokerId = row.BrokerId;
        await _db.SaveChangesAsync(cancellationToken);
        return row.BrokerId;
    }

    public async Task<int> GetOrCreateBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var existing = await GetBrokerIdAsync(userId, cancellationToken);
        if (existing.HasValue) return existing.Value;
        var user = await _db.Users.SingleAsync(x => x.Id == userId, cancellationToken);
        var brokerageName = user.ReraRegistrationNumber ?? "Independent Broker";
        await _db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO brokers (name, phone_number, brokerage_name) VALUES ({user.Name}, {user.MobileNumber}, {brokerageName})", cancellationToken);
        var brokerId = await GetBrokerIdAsync(userId, cancellationToken) ?? throw new InvalidOperationException("Broker profile could not be created.");
        user.BrokerId = brokerId;
        if (!await _db.CreditWallets.AnyAsync(w => w.BrokerId == brokerId, cancellationToken))
        {
            _db.CreditWallets.Add(new CreditWallet { BrokerId = brokerId, FreeCreditsBalance = 10, PaidCreditsBalance = 0 });
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        return brokerId;
    }

    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());
    private sealed class BrokerIdRow { public int BrokerId { get; set; } }
}
