using Microsoft.EntityFrameworkCore;
using SalaryTelegramBot.Api.Models;

namespace SalaryTelegramBot.Api.Data;

public static class SeedData
{
    private const long ChatId = 454887189;

    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.Transactions.AnyAsync())
            return;

        db.BotSettings.Add(new BotSettings
        {
            ChatId = ChatId,
            CheckHour = 12,
            CheckMinute = 0,
            IsNdflEnabled = true,
            NdflStartDay = 1,
            NdflStartMonth = 1,
            NdflStartYear = 2026,
            CalculationStartDay = 30,
            CalculationStartMonth = 11,
            CalculationStartYear = 2025
        });

        db.AccrualRules.AddRange(
            new AccrualRule { ChatId = ChatId, DayOfMonth = 15, Amount = 75000 },
            new AccrualRule { ChatId = ChatId, DayOfMonth = 30, Amount = 75000 }
        );

        var salaries = new (DateTime Date, decimal Amount)[]
        {
            (new DateTime(2025, 11, 30), 85000),
            (new DateTime(2025, 12, 15), 75000),
            (new DateTime(2025, 12, 30), 75000),
            (new DateTime(2026, 1, 15), 75000),
            (new DateTime(2026, 1, 30), 75000),
            (new DateTime(2026, 2, 15), 75000),
            (new DateTime(2026, 2, 28), 75000),
            (new DateTime(2026, 3, 15), 75000),
            (new DateTime(2026, 3, 30), 75000),
            (new DateTime(2026, 4, 15), 75000),
            (new DateTime(2026, 4, 30), 75000),
            (new DateTime(2026, 5, 15), 75000),
            (new DateTime(2026, 5, 30), 75000),
        };

        foreach (var (date, amount) in salaries)
        {
            db.Transactions.Add(new Transaction
            {
                ChatId = ChatId,
                Date = DateTime.SpecifyKind(date, DateTimeKind.Utc),
                Amount = amount,
                Type = TransactionType.Salary
            });
        }

        var payments = new DateTime[]
        {
            new(2025, 11, 30),
            new(2025, 12, 30),
            new(2026, 1, 30),
            new(2026, 2, 28),
        };

        foreach (var date in payments)
        {
            db.Transactions.Add(new Transaction
            {
                ChatId = ChatId,
                Date = DateTime.SpecifyKind(date, DateTimeKind.Utc),
                Amount = 100000,
                Type = TransactionType.Payment
            });
        }

        var ndflDates = new DateTime[]
        {
            new(2026, 1, 15),
            new(2026, 1, 30),
            new(2026, 2, 15),
            new(2026, 2, 28),
            new(2026, 3, 15),
            new(2026, 3, 30),
            new(2026, 4, 15),
            new(2026, 4, 30),
            new(2026, 5, 15),
            new(2026, 5, 30),
        };

        foreach (var date in ndflDates)
        {
            db.Transactions.Add(new Transaction
            {
                ChatId = ChatId,
                Date = DateTime.SpecifyKind(date, DateTimeKind.Utc),
                Amount = 11207,
                Type = TransactionType.Vat,
                Comment = "auto_ndfl"
            });
        }

        await db.SaveChangesAsync();
    }
}
