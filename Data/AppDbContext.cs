using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using PropSeekr.Models;

namespace PropSeekr.Data;

public class AppDbContext : DbContext
{
    private static readonly Guid SeedAdminId = Guid.Parse("b0ef71c1-13ab-4070-9d85-86571adf59c8");
    private static readonly DateTime SeedAdminCreatedDate = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
    private const string SeedAdminPasswordHash = "PBKDF2-SHA256$100000$LVJ7kAGk7zNsEMneVYpBfyC8ZPENOZviao1yEc2gT1s=$hjnNN2d8W2s5SBkDGTAb+ct2M9qD+Csk7rNkZs9hlaM=";

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<User> Users => Set<User>();
    public DbSet<OtpVerification> OtpVerifications => Set<OtpVerification>();
    public DbSet<EmailOtpRecord> EmailOtpRecords => Set<EmailOtpRecord>();
    public DbSet<PropertyRequest> PropertyRequests => Set<PropertyRequest>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<UnlockedProperty> UnlockedProperties => Set<UnlockedProperty>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.MobileNumber)
            .IsUnique();

        modelBuilder.Entity<User>()
            .HasIndex(u => u.AadharNumber)
            .IsUnique();

        modelBuilder.Entity<User>()
            .HasIndex(u => u.PanCard)
            .IsUnique();

        modelBuilder.Entity<AdminUser>()
            .HasIndex(a => a.UserName)
            .IsUnique();

        modelBuilder.Entity<AdminUser>()
            .HasData(new AdminUser
            {
                Id = SeedAdminId,
                UserName = "admin",
                PasswordHash = SeedAdminPasswordHash,
                IsActive = true,
                CreatedDate = SeedAdminCreatedDate,
                ModifiedDate = SeedAdminCreatedDate
            });

        modelBuilder.Entity<OtpVerification>()
            .HasIndex(o => new { o.MobileNumber, o.OtpCode });

        // Map to the actual table name present in the database (OtpVerifications)
        modelBuilder.Entity<OtpVerification>()
            .ToTable("OtpVerifications");

        modelBuilder.Entity<OtpVerification>()
            .HasIndex(o => o.MobileNumber);

        modelBuilder.Entity<EmailOtpRecord>()
            .HasIndex(e => new { e.Email, e.Purpose, e.IsUsed, e.ExpiresAt });

        modelBuilder.Entity<EmailOtpRecord>()
            .HasIndex(e => e.ExpiresAt);

        // Configure PropertyRequest relationships and indexes
        modelBuilder.Entity<PropertyRequest>()
            .HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PropertyRequest>()
            .HasIndex(p => p.UserId);

        modelBuilder.Entity<PropertyRequest>()
            .HasIndex(p => p.TransactionType);

        modelBuilder.Entity<PropertyRequest>()
            .HasIndex(p => p.Category);

        modelBuilder.Entity<PropertyRequest>()
            .HasIndex(p => new { p.City, p.Locality });

        modelBuilder.Entity<PropertyRequest>()
            .HasIndex(p => p.PostedAt)
            .IsDescending();
        
        modelBuilder.Entity<PropertyRequest>()
            .HasIndex(p => p.BudgetMin);

        modelBuilder.Entity<PropertyRequest>()
            .HasIndex(p => p.BudgetMax);

        modelBuilder.Entity<PropertyRequest>()
            .HasIndex(p => p.PropertyTypesJson);

        // Configure the PostGIS geography point column for spatial distance queries
        modelBuilder.Entity<PropertyRequest>()
            .Property(p => p.Location)
            .HasColumnType("geography (point)");

        // Spatial index for fast distance filtering
        modelBuilder.Entity<PropertyRequest>()
            .HasIndex(p => p.Location)
            .HasMethod("GIST");

        // Configure PaymentTransaction relationships and indexes
        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PaymentTransaction>()
            .HasIndex(p => p.RazorpayOrderId)
            .IsUnique();

        modelBuilder.Entity<PaymentTransaction>()
            .HasIndex(p => p.Receipt)
            .IsUnique();

        // Configure UnlockedProperty relationships and unique index
        modelBuilder.Entity<UnlockedProperty>()
            .HasOne(u => u.User)
            .WithMany()
            .HasForeignKey(u => u.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UnlockedProperty>()
            .HasOne(u => u.PropertyRequest)
            .WithMany()
            .HasForeignKey(u => u.PropertyRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UnlockedProperty>()
            .HasIndex(u => new { u.UserId, u.PropertyRequestId })
            .IsUnique();
    }
}
