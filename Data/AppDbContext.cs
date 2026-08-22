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
    public DbSet<Broker> Brokers => Set<Broker>();
    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<ListingRequirement> ListingRequirements => Set<ListingRequirement>();
    public DbSet<ListingSize> ListingSizes => Set<ListingSize>();
    public DbSet<Requirement> Requirements => Set<Requirement>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<CreditWallet> CreditWallets => Set<CreditWallet>();
    public DbSet<CreditPack> CreditPacks => Set<CreditPack>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CreditTransaction> CreditTransactions => Set<CreditTransaction>();
    public DbSet<MatchConfirmation> MatchConfirmations => Set<MatchConfirmation>();
    public DbSet<Reveal> Reveals => Set<Reveal>();
    public DbSet<BrokerNotification> BrokerNotifications => Set<BrokerNotification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<Dispute> Disputes => Set<Dispute>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<Deal> Deals => Set<Deal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var dateTimeConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
            v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc));

        var nullableDateTimeConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>(
            v => !v.HasValue ? v : (v.Value.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)),
            v => !v.HasValue ? v : (v.Value.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.IsKeyless)
            {
                continue;
            }

            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(dateTimeConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(nullableDateTimeConverter);
                }
            }
        }

        modelBuilder.Entity<OtpVerification>().ToTable("OtpVerifications");
        modelBuilder.Entity<User>().HasIndex(x => x.MobileNumber).IsUnique();
        modelBuilder.Entity<User>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<User>().HasOne(u => u.Broker).WithMany().HasForeignKey(u => u.BrokerId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<User>().HasIndex(u => u.AadharNumber).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.PanCard).IsUnique();
        modelBuilder.Entity<AdminUser>().HasIndex(a => a.UserName).IsUnique();
        modelBuilder.Entity<AdminUser>().HasData(new AdminUser
        {
            Id = SeedAdminId,
            UserName = "admin",
            PasswordHash = SeedAdminPasswordHash,
            IsActive = true,
            CreatedDate = SeedAdminCreatedDate,
            ModifiedDate = SeedAdminCreatedDate
        });
        modelBuilder.Entity<OtpVerification>().HasIndex(o => new { o.MobileNumber, o.OtpCode });
        modelBuilder.Entity<OtpVerification>().HasIndex(o => o.MobileNumber);
        modelBuilder.Entity<EmailOtpRecord>().HasIndex(e => new { e.Email, e.Purpose, e.IsUsed, e.ExpiresAt });
        modelBuilder.Entity<EmailOtpRecord>().HasIndex(e => e.ExpiresAt);

        modelBuilder.Entity<PropertyRequest>().HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PropertyRequest>().HasIndex(p => p.UserId);
        modelBuilder.Entity<PropertyRequest>().HasIndex(p => p.TransactionType);
        modelBuilder.Entity<PropertyRequest>().HasIndex(p => p.ListingType);
        modelBuilder.Entity<PropertyRequest>().HasIndex(p => p.Category);
        modelBuilder.Entity<PropertyRequest>().HasIndex(p => new { p.City, p.Locality });
        modelBuilder.Entity<PropertyRequest>().HasIndex(p => p.PostedAt).IsDescending();
        modelBuilder.Entity<PropertyRequest>().HasIndex(p => p.BudgetMin);
        modelBuilder.Entity<PropertyRequest>().HasIndex(p => p.BudgetMax);
        modelBuilder.Entity<PropertyRequest>().HasIndex(p => p.PropertyTypesJson);
        modelBuilder.Entity<PropertyRequest>().Property(p => p.Location).HasColumnType("geography (point)");
        modelBuilder.Entity<PropertyRequest>().HasIndex(p => p.Location).HasMethod("GIST");

        modelBuilder.Entity<PaymentTransaction>().HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PaymentTransaction>().HasIndex(p => p.RazorpayOrderId).IsUnique();
        modelBuilder.Entity<PaymentTransaction>().HasIndex(p => p.Receipt).IsUnique();

        modelBuilder.Entity<UnlockedProperty>().HasOne(u => u.User).WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UnlockedProperty>().HasOne(u => u.PropertyRequest).WithMany().HasForeignKey(u => u.PropertyRequestId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UnlockedProperty>().HasIndex(u => new { u.UserId, u.PropertyRequestId }).IsUnique();

        modelBuilder.Entity<Broker>().HasIndex(b => b.PhoneNumber).IsUnique();

        modelBuilder.Entity<Listing>().HasOne(l => l.Broker).WithMany().HasForeignKey(l => l.BrokerId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Requirement>().HasOne(r => r.Broker).WithMany().HasForeignKey(r => r.BrokerId).OnDelete(DeleteBehavior.Cascade);

        // Configure Match (with lowercase table and column names)
        modelBuilder.Entity<Match>(e =>
        {
            e.ToTable("matches");
            e.HasKey(x => x.Id);
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

            e.HasOne(m => m.Listing).WithMany().HasForeignKey(m => m.ListingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.Requirement).WithMany().HasForeignKey(m => m.RequirementId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.ListingBroker).WithMany().HasForeignKey(m => m.ListingBrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.RequirementBroker).WithMany().HasForeignKey(m => m.RequirementBrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => m.Status);
            e.HasIndex(m => m.State);
        });

        // Configure MatchConfirmation
        modelBuilder.Entity<MatchConfirmation>(e =>
        {
            e.ToTable("match_confirmations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id");
            e.Property(x => x.MatchId).HasColumnName("match_id");
            e.Property(x => x.BrokerId).HasColumnName("broker_id");
            e.Property(x => x.AvailabilityConfirmed).HasColumnName("availability_confirmed");
            e.Property(x => x.PriceValid).HasColumnName("price_valid");
            e.Property(x => x.PriceNegotiable).HasColumnName("price_negotiable");
            e.Property(x => x.ReadyToConnect).HasColumnName("ready_to_connect");
            e.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at");
            e.Property(x => x.WindowExpiresAt).HasColumnName("window_expires_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.MatchId, x.BrokerId }).IsUnique();
            e.HasIndex(x => x.WindowExpiresAt);

            e.HasOne(c => c.Match).WithMany().HasForeignKey(c => c.MatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Broker).WithMany().HasForeignKey(c => c.BrokerId).OnDelete(DeleteBehavior.Cascade);
        });

        // Configure Reveal
        modelBuilder.Entity<Reveal>(e =>
        {
            e.ToTable("reveals");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id");
            e.Property(x => x.MatchId).HasColumnName("match_id");
            e.Property(x => x.RevealedAt).HasColumnName("revealed_at");
            e.HasIndex(x => x.MatchId).IsUnique();

            e.HasOne(r => r.Match).WithMany().HasForeignKey(r => r.MatchId).OnDelete(DeleteBehavior.Cascade);
        });

        // Configure CreditWallet
        modelBuilder.Entity<CreditWallet>(e =>
        {
            e.ToTable("credit_wallets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id");
            e.Property(x => x.BrokerId).HasColumnName("broker_id");
            e.Property(x => x.FreeCreditsBalance).HasColumnName("free_credits_balance");
            e.Property(x => x.PaidCreditsBalance).HasColumnName("paid_credits_balance");
            e.Property(x => x.FreeCreditsResetAt).HasColumnName("free_credits_reset_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.BrokerId).IsUnique();

            e.HasOne(w => w.Broker).WithMany().HasForeignKey(w => w.BrokerId).OnDelete(DeleteBehavior.Cascade);
        });

        // Configure CreditTransaction
        modelBuilder.Entity<CreditTransaction>(e =>
        {
            e.ToTable("credit_transactions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id");
            e.Property(x => x.BrokerId).HasColumnName("broker_id");
            e.Property(x => x.Type).HasColumnName("Type");
            e.Property(x => x.Amount).HasColumnName("Amount");
            e.Property(x => x.BalanceAfter).HasColumnName("balance_after");
            e.Property(x => x.ReferenceType).HasColumnName("reference_type");
            e.Property(x => x.ReferenceId).HasColumnName("reference_id");
            e.Property(x => x.Notes).HasColumnName("Notes");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
            e.HasIndex(t => new { t.BrokerId, t.CreatedAt });

            e.HasOne(t => t.Broker).WithMany().HasForeignKey(t => t.BrokerId).OnDelete(DeleteBehavior.Cascade);
        });

        // Configure CreditPack
        modelBuilder.Entity<CreditPack>(e =>
        {
            e.ToTable("credit_packs");
            e.Property(x => x.Id).HasColumnName("Id");
            e.Property(x => x.Name).HasColumnName("Name");
            e.Property(x => x.Credits).HasColumnName("Credits");
            e.Property(x => x.Price).HasColumnName("Price");
            e.Property(x => x.Active).HasColumnName("Active");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
        });

        // Configure Payment
        modelBuilder.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.Property(x => x.Id).HasColumnName("Id");
            e.Property(x => x.BrokerId).HasColumnName("broker_id");
            e.Property(x => x.CreditPackId).HasColumnName("credit_pack_id");
            e.Property(x => x.Amount).HasColumnName("amount");
            e.Property(x => x.Currency).HasColumnName("currency");
            e.Property(x => x.Gateway).HasColumnName("gateway");
            e.Property(x => x.GatewayTransactionId).HasColumnName("gateway_txn_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            e.HasOne(p => p.Broker).WithMany().HasForeignKey(p => p.BrokerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.CreditPack).WithMany().HasForeignKey(p => p.CreditPackId).OnDelete(DeleteBehavior.SetNull);
        });

        // Configure BrokerNotification, NotificationPreference, Dispute, Visit, Deal, ListingRequirement
        modelBuilder.Entity<Notification>().HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Notification>().HasIndex(n => n.UserId);
        modelBuilder.Entity<Notification>().HasIndex(n => n.CreatedAt).IsDescending();

        modelBuilder.Entity<BrokerNotification>().HasOne(n => n.Broker).WithMany().HasForeignKey(n => n.BrokerId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BrokerNotification>().HasIndex(n => new { n.BrokerId, n.ReadAt });

        modelBuilder.Entity<NotificationPreference>().HasOne(p => p.Broker).WithMany().HasForeignKey(p => p.BrokerId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NotificationPreference>().HasIndex(p => p.BrokerId).IsUnique();

        modelBuilder.Entity<Dispute>().HasOne(d => d.Broker).WithMany().HasForeignKey(d => d.BrokerId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Dispute>().HasOne(d => d.Transaction).WithMany().HasForeignKey(d => d.TransactionId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Dispute>().HasIndex(d => new { d.BrokerId, d.Status });

        modelBuilder.Entity<Visit>().HasOne(v => v.Match).WithMany().HasForeignKey(v => v.MatchId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Visit>().HasOne(v => v.MarkedByBroker).WithMany().HasForeignKey(v => v.MarkedByBrokerId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Deal>().HasOne(d => d.Match).WithMany().HasForeignKey(d => d.MatchId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Deal>().HasOne(d => d.MarkedByBroker).WithMany().HasForeignKey(d => d.MarkedByBrokerId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Deal>().HasIndex(d => d.MatchId).IsUnique();

        modelBuilder.Entity<ListingRequirement>().HasIndex(lr => new { lr.ListingId, lr.RequirementId }).IsUnique();
        modelBuilder.Entity<ListingRequirement>().HasOne(lr => lr.Listing).WithMany().HasForeignKey(lr => lr.ListingId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ListingRequirement>().HasOne(lr => lr.Requirement).WithMany().HasForeignKey(lr => lr.RequirementId).OnDelete(DeleteBehavior.Cascade);
    }
}
