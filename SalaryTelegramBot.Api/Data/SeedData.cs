using Microsoft.EntityFrameworkCore;

namespace SalaryTelegramBot.Api.Data;

public static class SeedData
{
    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.Transactions.AnyAsync())
            return;
    }
}
