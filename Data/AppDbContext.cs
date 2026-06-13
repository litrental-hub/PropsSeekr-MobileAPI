using Microsoft.EntityFrameworkCore;
using PropSeekr.Models;

namespace PropSeekr.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

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
    modelBuilder.Entity<OtpVerification>()
        .HasIndex(o => new { o.MobileNumber, o.OtpCode });

    modelBuilder.Entity<User>()
        .HasIndex(u => u.AadharNumber)
        .IsUnique();

        // Map to the actual table name present in the database (OtpVerifications)
        modelBuilder.Entity<OtpVerification>()
            .ToTable("OtpVerifications");

        modelBuilder.Entity<OtpVerification>()
            .HasIndex(o => o.MobileNumber);
    }
}
