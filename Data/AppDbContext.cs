using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using PropSeekr.Models;

namespace PropSeekr.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<User> Users => Set<User>();
    public DbSet<OtpVerification> OtpVerifications => Set<OtpVerification>();
    public DbSet<EmailOtpRecord> EmailOtpRecords => Set<EmailOtpRecord>();
    public DbSet<PropertyRequest> PropertyRequests => Set<PropertyRequest>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<UnlockedProperty> UnlockedProperties => Set<UnlockedProperty>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MatchConfirmation> MatchConfirmations => Set<MatchConfirmation>();
    public DbSet<MatchConnectionRequest> MatchConnectionRequests => Set<MatchConnectionRequest>();
    public DbSet<Reveal> Reveals => Set<Reveal>();
    public DbSet<CreditWallet> CreditWallets => Set<CreditWallet>();
    public DbSet<CreditTransaction> CreditTransactions => Set<CreditTransaction>();
    public DbSet<CreditPack> CreditPacks => Set<CreditPack>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Broker> Brokers => Set<Broker>();
    public DbSet<BrokerNotification> BrokerNotifications => Set<BrokerNotification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<MasterLocation> MasterLocations => Set<MasterLocation>();
    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<ListingDetail> ListingDetails => Set<ListingDetail>();
    public DbSet<ListingMedia> ListingMedia => Set<ListingMedia>();
    public DbSet<Requirement> Requirements => Set<Requirement>();
    public DbSet<EmbeddingJob> EmbeddingJobs => Set<EmbeddingJob>();
    public DbSet<BulkImportJob> BulkImportJobs => Set<BulkImportJob>();
    public DbSet<LocationRemediationJob> LocationRemediationJobs => Set<LocationRemediationJob>();
    public DbSet<ListingSize> ListingSizes => Set<ListingSize>();
    public DbSet<ListingRequirement> ListingRequirements => Set<ListingRequirement>();
    public DbSet<MatchStatus> MatchStatuses => Set<MatchStatus>();
    public DbSet<Deal> Deals => Set<Deal>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<Dispute> Disputes => Set<Dispute>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<OtpVerification>().ToTable("OtpVerifications");
        b.Entity<User>().HasIndex(x => x.MobileNumber).IsUnique();
        b.Entity<User>().HasIndex(x => x.Email).IsUnique();
        b.Entity<User>().HasIndex(x => x.AadharNumber).IsUnique();
        b.Entity<User>().HasIndex(x => x.PanCard).IsUnique();
        b.Entity<User>().Property(x => x.BrokerId).HasColumnName("BrokerId");
        b.Entity<User>().Property(x => x.Role).HasMaxLength(30).HasDefaultValue("User");
        b.Entity<User>().Property(x => x.UserName).HasMaxLength(100);
        b.Entity<User>().Property(x => x.IsActive).HasDefaultValue(true);
        b.Entity<Broker>().Property(x => x.ConfirmationComplianceRate).HasDefaultValue(100.00m);
        b.Entity<Broker>().Property(x => x.VisibilityPenaltyFlag).HasDefaultValue(false);
        b.Entity<Broker>().Property(x => x.ResponseScore).HasDefaultValue(100.00m);
        b.Entity<Broker>().Property(x => x.Status).HasDefaultValue("ACTIVE");
        b.Entity<Broker>().Property(x => x.CreatedAt).HasDefaultValueSql("NOW()");
        b.Entity<OtpVerification>().HasIndex(x => x.MobileNumber);
        b.Entity<OtpVerification>().HasIndex(x => new { x.MobileNumber, x.OtpCode });
        b.Entity<EmailOtpRecord>().HasIndex(x => x.ExpiresAt);
        b.Entity<EmailOtpRecord>().HasIndex(x => new { x.Email, x.Purpose, x.IsUsed, x.ExpiresAt });
        b.Entity<PaymentTransaction>().HasIndex(x => x.RazorpayOrderId).IsUnique();
        b.Entity<PaymentTransaction>().HasIndex(x => x.Receipt).IsUnique();
        b.Entity<PropertyRequest>().Property(x => x.Location).HasColumnType("geography (point)");
        b.Entity<PropertyRequest>().HasIndex(x => x.Location).HasMethod("GIST");
        b.Entity<PropertyRequest>().HasIndex(x => x.BudgetMin);
        b.Entity<PropertyRequest>().HasIndex(x => x.BudgetMax);
        b.Entity<PropertyRequest>().HasIndex(x => x.Category);
        b.Entity<PropertyRequest>().HasIndex(x => new { x.City, x.Locality });
        b.Entity<PropertyRequest>().HasIndex(x => x.PostedAt);
        b.Entity<PropertyRequest>().HasIndex(x => x.PropertyTypesJson);
        b.Entity<PropertyRequest>().HasIndex(x => x.TransactionType);
        b.Entity<UnlockedProperty>().HasIndex(x => new { x.UserId, x.PropertyRequestId }).IsUnique();

        b.Entity<MasterLocation>(e =>
        {
            e.ToTable("master");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.City, x.Area });
            e.Property(x => x.GeocodingStatus).HasDefaultValue("pending");
            e.Property(x => x.ReviewRequired).HasDefaultValue(false);
            e.HasIndex(x => new { x.GeocodingStatus, x.Id });
        });

        b.Entity<Listing>(e =>
        {
            e.Property(x => x.IsAvailable).HasDefaultValue(true);
            e.Property(x => x.LocationResolutionStatus).HasDefaultValue("missing");
            e.HasIndex(x => x.MasterId);
            e.HasIndex(x => x.ListingType);
            e.HasIndex(x => x.PropertyType);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => x.Price);
            e.HasIndex(x => new { x.LocationResolutionStatus, x.Id });
            e.HasOne(x => x.MasterLocation)
                .WithMany()
                .HasForeignKey(x => x.MasterId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Requirement>(e =>
        {
            e.Property(x => x.IsAvailable).HasDefaultValue(true);
            e.Property(x => x.LocationResolutionStatus).HasDefaultValue("missing");
            e.HasIndex(x => x.RequirementType);
            e.HasIndex(x => x.PropertyType);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => x.Budget);
            e.HasIndex(x => x.Configurations).HasMethod("GIN");
            e.HasIndex(x => x.PreferredLocalityIds).HasMethod("GIN");
            e.HasIndex(x => new { x.LocationResolutionStatus, x.Id });
        });

        b.Entity<Match>(e =>
        {
            e.ToTable("matches"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("matchid");
            e.Property(x => x.ListingId).HasColumnName("listing_id");
            e.Property(x => x.RequirementId).HasColumnName("requirement_id");
            e.Property(x => x.ListingBrokerId).HasColumnName("listing_broker_id");
            e.Property(x => x.RequirementBrokerId).HasColumnName("requirement_broker_id");
            e.Property(x => x.MatchScore).HasColumnName("match_score");
            e.Property(x => x.MatchTier).HasColumnName("match_tier").HasMaxLength(16);
            e.Property(x => x.ScoreBreakdownJson).HasColumnName("score_breakdown").HasColumnType("jsonb");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.State).HasColumnName("state").HasDefaultValue("matched");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.StatusUpdatedAt).HasColumnName("status_updated_at");
            e.Property(x => x.AiStatus).HasColumnName("ai_status");
            e.Property(x => x.AiConfidencePct).HasColumnName("ai_confidence_pct");
            e.Property(x => x.AiReasoning).HasColumnName("ai_reasoning");
            e.Property(x => x.AiFlagsJson).HasColumnName("ai_flags").HasColumnType("jsonb");
            e.Property(x => x.AiValidatedAt).HasColumnName("ai_validated_at");
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.State);
        });
        b.Entity<MatchConfirmation>(e => { e.ToTable("match_confirmations"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.MatchId).HasColumnName("match_id"); e.Property(x => x.BrokerId).HasColumnName("broker_id"); e.Property(x => x.AvailabilityConfirmed).HasColumnName("availability_confirmed"); e.Property(x => x.PriceValid).HasColumnName("price_valid"); e.Property(x => x.PriceNegotiable).HasColumnName("price_negotiable"); e.Property(x => x.ReadyToConnect).HasColumnName("ready_to_connect"); e.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at"); e.Property(x => x.WindowExpiresAt).HasColumnName("window_expires_at"); e.Property(x => x.CreatedAt).HasColumnName("created_at"); e.HasIndex(x => new { x.MatchId, x.BrokerId }).IsUnique(); e.HasIndex(x => x.WindowExpiresAt); });
        b.Entity<MatchConnectionRequest>(e =>
        {
            e.HasIndex(x => new { x.MatchId, x.Status });
            e.HasIndex(x => new { x.ReceivingBrokerId, x.Status });
            e.HasIndex(x => new { x.Status, x.ExpiresAt });
            e.HasOne(x => x.Match).WithMany().HasForeignKey(x => x.MatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.RequestingBroker).WithMany().HasForeignKey(x => x.RequestingBrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReceivingBroker).WithMany().HasForeignKey(x => x.ReceivingBrokerId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<BrokerNotification>(e =>
        {
            e.HasIndex(x => x.ConnectionRequestId);
            e.HasOne(x => x.ConnectionRequest).WithMany().HasForeignKey(x => x.ConnectionRequestId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("FK_notifications_connection_requests");
        });
        b.Entity<EmbeddingJob>(e =>
        {
            e.ToTable("embedding_jobs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Status, x.AvailableAt });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => new { x.EntityType, x.EntityId })
                .HasDatabaseName("UX_embedding_jobs_one_queued_per_entity")
                .IsUnique()
                .HasFilter("status = 'queued'");
        });
        b.Entity<BulkImportJob>(e => { e.ToTable("bulk_import_jobs"); e.HasKey(x => x.Id); e.Property(x => x.DefaultCity).HasDefaultValue("Indore"); e.HasIndex(x => new { x.Status, x.AvailableAt }); e.HasIndex(x => x.BrokerId); e.HasIndex(x => x.StorageKey).IsUnique(); e.HasOne(x => x.Broker).WithMany().HasForeignKey(x => x.BrokerId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<LocationRemediationJob>(e =>
        {
            e.ToTable("location_remediation_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasDefaultValue("queued");
            e.Property(x => x.Stage).HasDefaultValue("master");
            e.Property(x => x.DefaultCity).HasDefaultValue("Indore");
            e.Property(x => x.BatchSize).HasDefaultValue(25);
            e.HasIndex(x => new { x.Status, x.AvailableAt });
        });
        b.Entity<Reveal>(e => { e.ToTable("reveals"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.MatchId).HasColumnName("match_id"); e.Property(x => x.RevealedAt).HasColumnName("revealed_at"); e.HasIndex(x => x.MatchId).IsUnique(); });
        b.Entity<CreditWallet>(e => { e.ToTable("credit_wallets"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.BrokerId).HasColumnName("broker_id"); e.Property(x => x.FreeCreditsBalance).HasColumnName("free_credits_balance"); e.Property(x => x.PaidCreditsBalance).HasColumnName("paid_credits_balance"); e.Property(x => x.FreeCreditsResetAt).HasColumnName("free_credits_reset_at"); e.Property(x => x.CreatedAt).HasColumnName("created_at"); e.Property(x => x.UpdatedAt).HasColumnName("updated_at"); e.HasIndex(x => x.BrokerId).IsUnique(); });
        b.Entity<CreditTransaction>(e => { e.ToTable("credit_transactions"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.BrokerId).HasColumnName("broker_id"); e.Property(x => x.Type).HasColumnName("Type"); e.Property(x => x.Amount).HasColumnName("Amount"); e.Property(x => x.BalanceAfter).HasColumnName("balance_after"); e.Property(x => x.ReferenceType).HasColumnName("reference_type"); e.Property(x => x.ReferenceId).HasColumnName("reference_id"); e.Property(x => x.ReferenceKey).HasColumnName("reference_key").HasMaxLength(100); e.Property(x => x.Notes).HasColumnName("Notes"); e.Property(x => x.CreatedAt).HasColumnName("CreatedAt"); e.HasIndex(x => new { x.BrokerId, x.ReferenceType, x.ReferenceKey }).IsUnique().HasFilter("reference_key IS NOT NULL"); });
        b.Entity<CreditPack>(e => { e.ToTable("credit_packs"); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.Name).HasColumnName("Name"); e.Property(x => x.Credits).HasColumnName("Credits"); e.Property(x => x.Price).HasColumnName("Price"); e.Property(x => x.Active).HasColumnName("Active"); e.Property(x => x.CreatedAt).HasColumnName("CreatedAt"); });
        b.Entity<ListingDetail>(e =>
        {
            e.HasKey(x => x.ListingId);
            e.Property(x => x.ListingId).ValueGeneratedNever();
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasOne(x => x.Listing).WithOne(x => x.Detail).HasForeignKey<ListingDetail>(x => x.ListingId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ListingMedia>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ListingId, x.SortOrder });
            e.HasOne(x => x.Listing).WithMany(x => x.Media).HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ListingRequirement>().HasIndex(x => new { x.ListingId, x.RequirementId }).IsUnique();
        b.Entity<CreditTransaction>().HasIndex(x => new { x.BrokerId, x.CreatedAt });
        b.Entity<NotificationPreference>().HasIndex(x => x.BrokerId).IsUnique();
        b.Entity<Deal>().HasIndex(x => x.MatchId).IsUnique();
        b.Entity<Dispute>().HasIndex(x => new { x.BrokerId, x.Status });
        b.Entity<Notification>().HasIndex(x => x.CreatedAt);
        b.Entity<Payment>(e => { e.ToTable("payments"); e.Property(x => x.Id).HasColumnName("Id"); e.Property(x => x.BrokerId).HasColumnName("broker_id"); e.Property(x => x.CreditPackId).HasColumnName("credit_pack_id"); e.Property(x => x.Amount).HasColumnName("amount"); e.Property(x => x.Currency).HasColumnName("currency"); e.Property(x => x.Gateway).HasColumnName("gateway"); e.Property(x => x.GatewayTxnId).HasColumnName("gateway_txn_id"); e.Property(x => x.Status).HasColumnName("status"); e.Property(x => x.CreatedAt).HasColumnName("created_at"); e.Property(x => x.UpdatedAt).HasColumnName("updated_at"); });
    }
}
