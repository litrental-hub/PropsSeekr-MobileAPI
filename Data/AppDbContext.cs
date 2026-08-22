using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using PropSeekr.Models;

namespace PropSeekr.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<User> Users => Set<User>();
    public DbSet<OtpVerification> OtpVerifications => Set<OtpVerification>();
    public DbSet<EmailOtpRecord> EmailOtpRecords => Set<EmailOtpRecord>();
    public DbSet<PropertyRequest> PropertyRequests => Set<PropertyRequest>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<UnlockedProperty> UnlockedProperties => Set<UnlockedProperty>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MatchConfirmation> MatchConfirmations => Set<MatchConfirmation>();
    public DbSet<Reveal> Reveals => Set<Reveal>();
    public DbSet<CreditWallet> CreditWallets => Set<CreditWallet>();
    public DbSet<CreditTransaction> CreditTransactions => Set<CreditTransaction>();
    public DbSet<CreditPack> CreditPacks => Set<CreditPack>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<OtpVerification>().ToTable("OtpVerifications");
        b.Entity<User>().HasIndex(x => x.MobileNumber).IsUnique();
        b.Entity<User>().HasIndex(x => x.Email).IsUnique();
        b.Entity<PropertyRequest>().Property(x => x.Location).HasColumnType("geography (point)");
        b.Entity<PropertyRequest>().HasIndex(x => x.Location).HasMethod("GIST");
        b.Entity<UnlockedProperty>().HasIndex(x => new { x.UserId, x.PropertyRequestId }).IsUnique();

        b.Entity<Match>(e =>
        {
            e.ToTable("matches"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("matchid");
            e.Property(x => x.ListingId).HasColumnName("listing_id");
            e.Property(x => x.RequirementId).HasColumnName("requirement_id");
            e.Property(x => x.ListingBrokerId).HasColumnName("listing_broker_id");
            e.Property(x => x.RequirementBrokerId).HasColumnName("requirement_broker_id");
            e.Property(x => x.MatchScore).HasColumnName("match_score");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.State).HasColumnName("state");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.StatusUpdatedAt).HasColumnName("status_updated_at");
        });
        b.Entity<MatchConfirmation>(e => { e.ToTable("match_confirmations"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.MatchId).HasColumnName("match_id"); e.Property(x => x.BrokerId).HasColumnName("broker_id"); e.Property(x => x.AvailabilityConfirmed).HasColumnName("availability_confirmed"); e.Property(x => x.PriceValid).HasColumnName("price_valid"); e.Property(x => x.PriceNegotiable).HasColumnName("price_negotiable"); e.Property(x => x.ReadyToConnect).HasColumnName("ready_to_connect"); e.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at"); e.Property(x => x.WindowExpiresAt).HasColumnName("window_expires_at"); e.Property(x => x.CreatedAt).HasColumnName("created_at"); e.HasIndex(x => new { x.MatchId, x.BrokerId }).IsUnique(); });
        b.Entity<Reveal>(e => { e.ToTable("reveals"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.MatchId).HasColumnName("match_id"); e.Property(x => x.RevealedAt).HasColumnName("revealed_at"); e.HasIndex(x => x.MatchId).IsUnique(); });
        b.Entity<CreditWallet>(e => { e.ToTable("credit_wallets"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.BrokerId).HasColumnName("broker_id"); e.Property(x => x.FreeCreditsBalance).HasColumnName("free_credits_balance"); e.Property(x => x.PaidCreditsBalance).HasColumnName("paid_credits_balance"); e.Property(x => x.FreeCreditsResetAt).HasColumnName("free_credits_reset_at"); e.Property(x => x.CreatedAt).HasColumnName("created_at"); e.Property(x => x.UpdatedAt).HasColumnName("updated_at"); e.HasIndex(x => x.BrokerId).IsUnique(); });
        b.Entity<CreditTransaction>(e => { e.ToTable("credit_transactions"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.BrokerId).HasColumnName("broker_id"); e.Property(x => x.Type).HasColumnName("Type"); e.Property(x => x.Amount).HasColumnName("Amount"); e.Property(x => x.BalanceAfter).HasColumnName("balance_after"); e.Property(x => x.ReferenceType).HasColumnName("reference_type"); e.Property(x => x.ReferenceId).HasColumnName("reference_id"); e.Property(x => x.Notes).HasColumnName("Notes"); e.Property(x => x.CreatedAt).HasColumnName("CreatedAt"); });
        b.Entity<CreditPack>(e => { e.ToTable("credit_packs"); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.Name).HasColumnName("Name"); e.Property(x => x.Credits).HasColumnName("Credits"); e.Property(x => x.Price).HasColumnName("Price"); e.Property(x => x.Active).HasColumnName("Active"); e.Property(x => x.CreatedAt).HasColumnName("CreatedAt"); });
        b.Entity<Payment>(e => { e.ToTable("payments"); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.BrokerId).HasColumnName("broker_id"); e.Property(x => x.CreditPackId).HasColumnName("credit_pack_id"); e.Property(x => x.Amount).HasColumnName("amount"); e.Property(x => x.Currency).HasColumnName("currency"); e.Property(x => x.Gateway).HasColumnName("gateway"); e.Property(x => x.GatewayTransactionId).HasColumnName("gateway_txn_id"); e.Property(x => x.Status).HasColumnName("status"); e.Property(x => x.CreatedAt).HasColumnName("created_at"); e.Property(x => x.UpdatedAt).HasColumnName("updated_at"); });
    }
}
