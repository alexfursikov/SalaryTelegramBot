using Microsoft.EntityFrameworkCore;
using SalaryTelegramBot.Api.Models;

namespace SalaryTelegramBot.Api.Data;

public class AppDbContext : DbContext
{
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<AccrualRule> AccrualRules => Set<AccrualRule>();
    public DbSet<BotSettings> BotSettings => Set<BotSettings>();

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccrualRule>()
            .HasIndex(x => new { x.ChatId, x.DayOfMonth })
            .IsUnique();

        modelBuilder.Entity<BotSettings>()
            .HasIndex(x => x.ChatId)
            .IsUnique();

        modelBuilder.Entity<Transaction>()
            .Property(x => x.Date)
            .HasConversion(
                v => v.Kind == DateTimeKind.Utc
                    ? v
                    : v.Kind == DateTimeKind.Local
                        ? v.ToUniversalTime()
                        : DateTime.SpecifyKind(v, DateTimeKind.Utc),
                v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        modelBuilder.Entity<Transaction>()
            .HasIndex(x => x.ChatId);
    }
}
