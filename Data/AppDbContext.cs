using Microsoft.EntityFrameworkCore;
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

        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique()
            .HasFilter("\"Email\" IS NOT NULL");

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
    }
}
