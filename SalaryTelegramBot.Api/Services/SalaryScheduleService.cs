using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalaryTelegramBot.Api.Configuration;
using SalaryTelegramBot.Api.Data;
using SalaryTelegramBot.Api.Models;

namespace SalaryTelegramBot.Api.Services;

public class SalaryScheduleService
{
    private readonly AppDbContext _db;
    private readonly SalarySettings _defaultSettings;
    private static readonly ConcurrentDictionary<long, BotSettings> _settingsCache = new();
    private static readonly HashSet<long> _seededChats = new();

    public SalaryScheduleService(AppDbContext db, IOptions<SalarySettings> options)
    {
        _db = db;
        _defaultSettings = options.Value;
    }

    public async Task EnsureSeededForChatAsync(long chatId)
    {
        if (_seededChats.Contains(chatId))
            return;

        if (!await _db.BotSettings.AnyAsync(x => x.ChatId == chatId))
        {
            _db.BotSettings.Add(new BotSettings
            {
                ChatId = chatId,
                CheckHour = _defaultSettings.CheckHour,
                CheckMinute = _defaultSettings.CheckMinute,
                IsNdflEnabled = false
            });
        }

        if (!await _db.AccrualRules.AnyAsync(x => x.ChatId == chatId))
        {
            foreach (var schedule in _defaultSettings.Schedules)
            {
                foreach (var day in schedule.Days)
                {
                    _db.AccrualRules.Add(new AccrualRule
                    {
                        ChatId = chatId,
                        DayOfMonth = day,
                        Amount = schedule.Amount
                    });
                }
            }
        }

        await _db.SaveChangesAsync();
        _seededChats.Add(chatId);
    }

    public async Task<List<AccrualRule>> GetRulesAsync(long chatId)
    {
        return await _db.AccrualRules
            .Where(x => x.ChatId == chatId)
            .OrderBy(x => x.DayOfMonth)
            .ToListAsync();
    }

    public async Task<(int Hour, int Minute)> GetCheckTimeAsync(long chatId)
    {
        var settings = await GetOrCreateBotSettingsAsync(chatId);
        return (settings.CheckHour, settings.CheckMinute);
    }

    public async Task<DateTime?> GetCalculationStartDateAsync(long chatId)
    {
        var settings = await GetOrCreateBotSettingsAsync(chatId);
        if (settings.CalculationStartYear is null || settings.CalculationStartMonth is null)
            return null;

        return new DateTime(
            settings.CalculationStartYear.Value,
            settings.CalculationStartMonth.Value,
            settings.CalculationStartDay ?? 1);
    }

    public async Task<string> SetCalculationStartMonthAsync(long chatId, int month, int year)
    {
        return await SetCalculationStartDateAsync(chatId, 1, month, year);
    }

    public async Task<string> SetCalculationStartDateAsync(long chatId, int day, int month, int year)
    {
        if (month is < 1 or > 12)
            return "Месяц должен быть от 1 до 12.";

        if (year is < 2000 or > 2100)
            return "Год должен быть в диапазоне 2000-2100.";

        if (day is < 1 or > 31)
            return "День должен быть от 1 до 31.";

        DateTime startDate;
        try
        {
            startDate = new DateTime(year, month, day);
        }
        catch
        {
            return "Некорректная дата.";
        }

        var settings = await GetOrCreateBotSettingsAsync(chatId);
        settings.CalculationStartDay = day;
        settings.CalculationStartMonth = month;
        settings.CalculationStartYear = year;
        await _db.SaveChangesAsync();
        _settingsCache.TryRemove(chatId, out _);

        return $"Период расчета установлен: с {startDate:dd.MM.yyyy}.";
    }

    public async Task<DateTime?> GetNdflStartDateAsync(long chatId)
    {
        var settings = await GetOrCreateBotSettingsAsync(chatId);
        if (settings.NdflStartYear is null || settings.NdflStartMonth is null)
            return null;

        return new DateTime(
            settings.NdflStartYear.Value,
            settings.NdflStartMonth.Value,
            settings.NdflStartDay ?? 1);
    }

    public async Task<string> SetNdflStartDateAsync(long chatId, int day, int month, int year)
    {
        if (month is < 1 or > 12)
            return "Месяц должен быть от 1 до 12.";

        if (year is < 2000 or > 2100)
            return "Год должен быть в диапазоне 2000-2100.";

        if (day is < 1 or > 31)
            return "День должен быть от 1 до 31.";

        DateTime startDate;
        try
        {
            startDate = new DateTime(year, month, day);
        }
        catch
        {
            return "Некорректная дата.";
        }

        var settings = await GetOrCreateBotSettingsAsync(chatId);
        settings.NdflStartDay = day;
        settings.NdflStartMonth = month;
        settings.NdflStartYear = year;
        await _db.SaveChangesAsync();
        _settingsCache.TryRemove(chatId, out _);

        return $"Дата старта НДФЛ: с {startDate:dd.MM.yyyy}.";
    }

    public async Task<string> FormatScheduleAsync(long chatId)
    {
        await EnsureSeededForChatAsync(chatId);

        var rules = await GetRulesAsync(chatId);
        var (hour, minute) = await GetCheckTimeAsync(chatId);
        var sym = SalaryService.GetCurrencySymbol(await GetCurrencyAsync(chatId));

        var lines = rules.Count == 0
            ? ["(пусто — начисления не настроены)"]
            : rules.Select(r =>
                $"• {r.DayOfMonth}-е число (или последний день месяца) — {r.Amount:N0} {sym}").ToArray();

        return
$"""
Правила плановых начислений

Время проверки: {hour:D2}:{minute:D2}
Дата начала расчета: {await GetCalculationStartTextAsync(chatId)}
НДФЛ: {(await IsNdflEnabledAsync(chatId) ? "включен" : "выключен")}
Дата старта НДФЛ: {await GetNdflStartTextAsync(chatId)}

{string.Join("\n", lines)}
""";
    }

    public async Task<string> AddOrUpdateRuleAsync(long chatId, int day, decimal amount)
    {
        if (day is < 1 or > 31)
            return "День должен быть от 1 до 31.";

        var rule = await _db.AccrualRules
            .FirstOrDefaultAsync(x => x.ChatId == chatId && x.DayOfMonth == day);

        if (rule is null)
        {
            _db.AccrualRules.Add(new AccrualRule
            {
                ChatId = chatId,
                DayOfMonth = day,
                Amount = amount
            });
        }
        else
        {
            rule.Amount = amount;
        }

        await _db.SaveChangesAsync();
        var sym = SalaryService.GetCurrencySymbol(await GetCurrencyAsync(chatId));
        return $"Начисление {day}-го числа: {amount:N0} {sym}";
    }

    public async Task<string> RemoveRuleAsync(long chatId, int day)
    {
        var rule = await _db.AccrualRules
            .FirstOrDefaultAsync(x => x.ChatId == chatId && x.DayOfMonth == day);

        if (rule is null)
            return $"Правило для {day}-го числа не найдено.";

        _db.AccrualRules.Remove(rule);
        await _db.SaveChangesAsync();
        return $"Удалено начисление {day}-го числа.";
    }

    public async Task<string> SetCheckTimeAsync(long chatId, int hour, int minute)
    {
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return "Некорректное время.";

        var settings = await GetOrCreateBotSettingsAsync(chatId);
        settings.CheckHour = hour;
        settings.CheckMinute = minute;
        await _db.SaveChangesAsync();
        _settingsCache.TryRemove(chatId, out _);

        return $"Время проверки: {hour:D2}:{minute:D2}";
    }

    public async Task<string> ToggleNdflAsync(long chatId)
    {
        var settings = await GetOrCreateBotSettingsAsync(chatId);
        settings.IsNdflEnabled = !settings.IsNdflEnabled;
        await _db.SaveChangesAsync();
        _settingsCache.TryRemove(chatId, out _);
        return $"НДФЛ: {(settings.IsNdflEnabled ? "включен" : "выключен")}.";
    }

    public async Task<bool> IsNdflEnabledAsync(long chatId)
    {
        var settings = await GetOrCreateBotSettingsAsync(chatId);
        return settings.IsNdflEnabled;
    }

    public async Task ApplyRulesForCurrentTimeAsync(SalaryService salaryService)
    {
        var today = DateTime.Today;
        var now = DateTime.Now;
        var currentHour = now.Hour;
        var currentMinute = now.Minute;

        var settings = await _db.BotSettings.ToListAsync();

        foreach (var setting in settings
                     .Where(s => s.CheckHour == currentHour && s.CheckMinute == currentMinute))
        {
            var rules = await _db.AccrualRules
                .Where(r => r.ChatId == setting.ChatId)
                .ToListAsync();

            foreach (var rule in rules)
            {
                var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
                var targetDay = Math.Min(rule.DayOfMonth, daysInMonth);
                if (today.Day != targetDay)
                    continue;

                await salaryService.AddSalary(setting.ChatId, rule.Amount, today);
            }
        }
    }

    public async Task<string> RecalculateAccrualsAsync(long chatId, SalaryService salaryService)
    {
        await EnsureSeededForChatAsync(chatId);

        var rules = await GetRulesAsync(chatId);
        if (rules.Count == 0)
            return "Нет правил начисления для пересчета.";

        var today = DateTime.Today;

        var configuredStartDate = await GetCalculationStartDateAsync(chatId);
        var startDate = configuredStartDate
                        ?? await _db.Transactions
                            .Where(x => x.ChatId == chatId)
                            .OrderBy(x => x.Date)
                            .Select(x => x.Date.Date)
                            .FirstOrDefaultAsync();

        if (startDate == default)
            startDate = new DateTime(today.Year, today.Month, 1);

        var existingSalaryDates = await _db.Transactions
            .Where(x => x.ChatId == chatId && x.Type == TransactionType.Salary)
            .Where(x => x.Date.Date >= startDate.Date && x.Date.Date <= today)
            .Select(x => x.Date.Date)
            .ToListAsync();

        var existing = existingSalaryDates.ToHashSet();
        var added = 0;

        var monthCursor = new DateTime(startDate.Year, startDate.Month, 1);

        while (monthCursor <= today)
        {
            var daysInMonth = DateTime.DaysInMonth(monthCursor.Year, monthCursor.Month);

            foreach (var rule in rules)
            {
                var targetDay = Math.Min(rule.DayOfMonth, daysInMonth);
                var accrualDate = new DateTime(monthCursor.Year, monthCursor.Month, targetDay);
                if (accrualDate < startDate.Date || accrualDate > today || existing.Contains(accrualDate))
                    continue;

                await salaryService.AddSalary(chatId, rule.Amount, accrualDate);
                existing.Add(accrualDate);
                added++;
            }

            monthCursor = monthCursor.AddMonths(1);
        }

        var ndflChanged = await salaryService.RecalculateNdflForRange(chatId, startDate, DateTime.Now);
        return $"Пересчет завершен. Добавлено начислений: {added}, пересчитано НДФЛ: {ndflChanged}.";
    }

    private async Task<BotSettings> GetOrCreateBotSettingsAsync(long chatId)
    {
        if (_settingsCache.TryGetValue(chatId, out var cached))
            return cached;

        var settings = await _db.BotSettings
            .FirstOrDefaultAsync(x => x.ChatId == chatId);

        if (settings is null)
        {
            settings = new BotSettings
            {
                ChatId = chatId,
                CheckHour = _defaultSettings.CheckHour,
                CheckMinute = _defaultSettings.CheckMinute,
                IsNdflEnabled = false
            };
            _db.BotSettings.Add(settings);
            await _db.SaveChangesAsync();
        }

        _settingsCache[chatId] = settings;
        return settings;
    }

    private async Task<string> GetCalculationStartTextAsync(long chatId)
    {
        var startDate = await GetCalculationStartDateAsync(chatId);
        return startDate is null ? "за все время" : $"с {startDate:dd.MM.yyyy}";
    }

    private async Task<string> GetNdflStartTextAsync(long chatId)
    {
        var startDate = await GetNdflStartDateAsync(chatId);
        return startDate is null ? "с первого начисления" : $"с {startDate:dd.MM.yyyy}";
    }

    public async Task<string> GetCurrencyAsync(long chatId)
    {
        var settings = await GetOrCreateBotSettingsAsync(chatId);
        return string.IsNullOrEmpty(settings.Currency) ? "RUB" : settings.Currency;
    }

    public async Task<(string Message, bool Converted)> SetCurrencyAsync(long chatId, string currency, NbrbRateService rateService)
    {
        var settings = await GetOrCreateBotSettingsAsync(chatId);
        var oldCurrency = string.IsNullOrEmpty(settings.Currency) ? "RUB" : settings.Currency;

        if (oldCurrency == currency)
            return ($"Валюта уже установлена: {currency}", false);

        var (fromRate, toRate) = await rateService.GetRatesAsync(oldCurrency, currency);
        if (fromRate is null || toRate is null)
            return ("Не удалось получить курс валюты из НБ РБ.", false);

        var transactions = await _db.Transactions
            .Where(x => x.ChatId == chatId)
            .ToListAsync();

        var converted = 0;
        foreach (var tx in transactions)
        {
            tx.Amount = rateService.Convert(tx.Amount, fromRate.Value, toRate.Value);
            converted++;
        }

        var rules = await _db.AccrualRules
            .Where(x => x.ChatId == chatId)
            .ToListAsync();

        foreach (var rule in rules)
        {
            rule.Amount = rateService.Convert(rule.Amount, fromRate.Value, toRate.Value);
        }

        settings.Currency = currency;
        await _db.SaveChangesAsync();
        _settingsCache[chatId] = settings;

        var sym = SalaryService.GetCurrencySymbol(currency);
        return ($"✅ Валюта: {sym} ({currency})\nКонвертировано {converted} записей по курсу: 1 {oldCurrency} = {toRate / fromRate:N4} {currency}", true);
    }
}
