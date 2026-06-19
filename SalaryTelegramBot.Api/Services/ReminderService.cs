using Microsoft.EntityFrameworkCore;
using SalaryTelegramBot.Api.Data;
using SalaryTelegramBot.Api.Models;
using Telegram.Bot;

namespace SalaryTelegramBot.Api.Services;

public class ReminderService
{
    private readonly AppDbContext _db;
    private readonly TelegramBotClient _bot;
    private readonly ILogger<ReminderService> _logger;
    private readonly EncryptionService _encryption;
    private readonly UserKeyCache _keyCache;

    public ReminderService(
        AppDbContext db,
        TelegramBotClient bot,
        ILogger<ReminderService> logger,
        EncryptionService encryption,
        UserKeyCache keyCache)
    {
        _db = db;
        _bot = bot;
        _logger = logger;
        _encryption = encryption;
        _keyCache = keyCache;
    }

    public async Task CheckAndSendRemindersAsync(CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var currentDay = today.Day;

        var settings = await _db.BotSettings.ToListAsync(ct);

        foreach (var setting in settings)
        {
            var key = _keyCache.GetKey(setting.ChatId);
            var rules = await _db.AccrualRules
                .Where(r => r.ChatId == setting.ChatId)
                .ToListAsync(ct);

            foreach (var rule in rules)
            {
                var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
                var targetDay = Math.Min(rule.DayOfMonth, daysInMonth);

                if (currentDay != targetDay)
                    continue;

                var hasAccrualToday = await _db.Transactions.AnyAsync(t =>
                    t.ChatId == setting.ChatId &&
                    t.Type == TransactionType.Salary &&
                    t.Date.Date == today.Date, ct);

                if (!hasAccrualToday)
                    continue;

                if (key is null)
                    continue;

                var calcStart = await GetCalculationStartDateAsync(setting.ChatId, ct);

                var transactions = await _db.Transactions
                    .Where(t => t.ChatId == setting.ChatId)
                    .Where(t => calcStart == null || t.Date >= calcStart.Value)
                    .ToListAsync(ct);

                foreach (var tx in transactions)
                {
                    if (tx.EncryptedAmount is not null)
                        tx.Amount = _encryption.Decrypt(tx.EncryptedAmount, key);
                }

                var balance = transactions
                    .Sum(t => t.Type == TransactionType.Salary || t.Type == TransactionType.Vat
                        ? t.Amount
                        : -t.Amount);

                if (balance <= 0)
                    continue;

                try
                {
                    var sym = SalaryService.GetCurrencySymbol(
                        string.IsNullOrEmpty(setting.Currency) ? "RUB" : setting.Currency);

                    await _bot.SendMessage(
                        setting.ChatId,
                        $"Напоминание: текущий долг по зарплате: {balance:N0} {sym}.",
                        cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send reminder to chat {ChatId}", setting.ChatId);
                }
            }
        }
    }

    private async Task<DateTime?> GetCalculationStartDateAsync(long chatId, CancellationToken ct)
    {
        var settings = await _db.BotSettings
            .FirstOrDefaultAsync(x => x.ChatId == chatId, ct);

        if (settings?.CalculationStartYear is null || settings.CalculationStartMonth is null)
            return null;

        return DateTime.SpecifyKind(
            new DateTime(
                settings.CalculationStartYear.Value,
                settings.CalculationStartMonth.Value,
                settings.CalculationStartDay ?? 1,
                0, 0, 0),
            DateTimeKind.Utc);
    }
}
