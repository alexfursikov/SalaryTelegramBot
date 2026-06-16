using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalaryTelegramBot.Api.Configuration;
using SalaryTelegramBot.Api.Data;
using SalaryTelegramBot.Api.Models;
using System.Text;

namespace SalaryTelegramBot.Api.Services;

public class SalaryService
{
    private readonly AppDbContext _db;
    private readonly SalarySettings _salarySettings;

    public SalaryService(AppDbContext db, IOptions<SalarySettings> salaryOptions)
    {
        _db = db;
        _salarySettings = salaryOptions.Value;
    }

    public async Task AddPayment(long chatId, decimal amount, DateTime date)
    {
        var normalizedDate = NormalizeToUtc(date);

        _db.Transactions.Add(new Transaction
        {
            ChatId = chatId,
            Amount = amount,
            Date = normalizedDate,
            Type = TransactionType.Payment
        });

        await _db.SaveChangesAsync();
    }

    public async Task AddSalary(long chatId, decimal amount, DateTime date)
    {
        var normalizedDate = NormalizeToUtc(date);

        bool exists = await _db.Transactions
            .AnyAsync(x =>
                x.ChatId == chatId &&
                x.Date.Date == normalizedDate.Date &&
                x.Type == TransactionType.Salary);

        if (exists)
            return;

        var ndflRate = _salarySettings.NdflPercent / 100m;
        if (ndflRate < 0m || ndflRate >= 1m)
            throw new InvalidOperationException("NdflPercent must be in range [0, 100).");

        // amount is treated as "net salary" (на руки)
        // NDFL = (net / (1 - rate)) - net
        var ndflAmount = Math.Round((amount / (1m - ndflRate)) - amount, 2, MidpointRounding.AwayFromZero);

        _db.Transactions.Add(new Transaction
        {
            ChatId = chatId,
            Amount = amount,
            Date = normalizedDate,
            Type = TransactionType.Salary
        });

        var ndflEnabled = await IsNdflEnabled(chatId);
        var ndflStartDate = await GetNdflStartDate(chatId);
        var shouldApplyNdfl = ndflEnabled && (ndflStartDate is null || normalizedDate >= ndflStartDate.Value);

        if (shouldApplyNdfl && ndflAmount > 0)
        {
            _db.Transactions.Add(new Transaction
            {
                ChatId = chatId,
                Amount = ndflAmount,
                Date = normalizedDate,
                Type = TransactionType.Vat,
                Comment = "auto_ndfl"
            });
        }

        await _db.SaveChangesAsync();
    }

    public async Task<int> RecalculateNdflForRange(long chatId, DateTime fromDate, DateTime toDate)
    {
        var fromUtc = NormalizeToUtc(fromDate);
        var toUtc = NormalizeToUtc(toDate);

        var salaries = await _db.Transactions
            .Where(x => x.ChatId == chatId && x.Type == TransactionType.Salary)
            .Where(x => x.Date >= fromUtc && x.Date <= toUtc)
            .ToListAsync();

        var allNdfl = await _db.Transactions
            .Where(x => x.ChatId == chatId && x.Type == TransactionType.Vat)
            .Where(x => x.Date >= fromUtc && x.Date <= toUtc)
            .ToListAsync();

        var autoNdfl = allNdfl
            .Where(x => x.Comment == "auto_ndfl")
            .ToList();

        var manualNdfl = allNdfl
            .Where(x => x.Comment == null || x.Comment != "auto_ndfl")
            .ToList();

        var changed = 0;
        if (manualNdfl.Count > 0)
        {
            _db.Transactions.RemoveRange(manualNdfl);
            changed += manualNdfl.Count;
            await _db.SaveChangesAsync();
        }

        var ndflEnabled = await IsNdflEnabled(chatId);
        var ndflStartDate = await GetNdflStartDate(chatId);
        if (!ndflEnabled)
        {
            if (autoNdfl.Count > 0)
            {
                _db.Transactions.RemoveRange(autoNdfl);
                await _db.SaveChangesAsync();
                changed += autoNdfl.Count;
            }

            return changed;
        }

        var ndflRate = _salarySettings.NdflPercent / 100m;
        if (ndflRate < 0m || ndflRate >= 1m)
            throw new InvalidOperationException("NdflPercent must be in range [0, 100).");

        var salaryByDate = salaries.ToDictionary(x => x.Date.Date, x => x);
        var autoNdflByDate = autoNdfl
            .GroupBy(x => x.Date.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (salaryDate, salaryTx) in salaryByDate)
        {
            if (ndflStartDate is not null && salaryTx.Date < ndflStartDate.Value)
            {
                autoNdflByDate.TryGetValue(salaryDate, out var preStartVatList);
                if (preStartVatList is { Count: > 0 })
                {
                    _db.Transactions.RemoveRange(preStartVatList);
                    changed += preStartVatList.Count;
                }

                continue;
            }

            var expected = Math.Round((salaryTx.Amount / (1m - ndflRate)) - salaryTx.Amount, 2, MidpointRounding.AwayFromZero);
            autoNdflByDate.TryGetValue(salaryDate, out var vatList);
            vatList ??= [];

            if (vatList.Count == 0)
            {
                _db.Transactions.Add(new Transaction
                {
                    ChatId = chatId,
                    Amount = expected,
                    Date = salaryTx.Date,
                    Type = TransactionType.Vat,
                    Comment = "auto_ndfl"
                });
                changed++;
                continue;
            }

            var first = vatList[0];
            if (first.Amount != expected || first.Date != salaryTx.Date)
            {
                first.Amount = expected;
                first.Date = salaryTx.Date;
                changed++;
            }

            if (vatList.Count > 1)
            {
                _db.Transactions.RemoveRange(vatList.Skip(1));
                changed += vatList.Count - 1;
            }
        }

        var orphanAutoNdfl = autoNdfl
            .Where(v => !salaryByDate.ContainsKey(v.Date.Date))
            .ToList();

        if (orphanAutoNdfl.Count > 0)
        {
            _db.Transactions.RemoveRange(orphanAutoNdfl);
            changed += orphanAutoNdfl.Count;
        }

        if (changed > 0)
            await _db.SaveChangesAsync();

        return changed;
    }

    public async Task<int> GetUserCount()
    {
        return await _db.BotSettings.Select(x => x.ChatId).Distinct().CountAsync();
    }

    public static string GetCurrencySymbol(string currency) => currency switch
    {
        "USD" => "$",
        "EUR" => "€",
        "BYN" => "бел. руб.",
        _ => "руб."
    };

    public async Task<string> GetStatus(long chatId, string currency = "RUB")
    {
        var calculationStartDate = await GetCalculationStartDate(chatId);

        var transactions = await _db.Transactions
            .Where(x => x.ChatId == chatId)
            .Where(x => calculationStartDate == null || x.Date >= calculationStartDate.Value)
            .ToListAsync();

        var salary = transactions
            .Where(x => x.Type == TransactionType.Salary)
            .Sum(x => x.Amount);

        var payments = transactions
            .Where(x => x.Type == TransactionType.Payment)
            .Sum(x => x.Amount);

        var ndfl = transactions
            .Where(x => x.Type == TransactionType.Vat)
            .Sum(x => x.Amount);

        var ndflEnabledForStatus = await IsNdflEnabled(chatId);
        if (!ndflEnabledForStatus)
            ndfl = 0;

        var balance = salary + ndfl - payments;
        var sym = GetCurrencySymbol(currency);

        return
$"""
Период: {(calculationStartDate is null ? "за все время" : $"с {calculationStartDate:dd.MM.yyyy}")}
Начислено: {salary:N0} {sym}
Выплачено: {payments:N0} {sym}
НДФЛ: {ndfl:N0} {sym}

Остаток: {balance:N0} {sym}
""";
    }

    public async Task<string> GetHistory(long chatId, string currency = "RUB")
    {
        var list = await _db.Transactions
            .Where(x => x.ChatId == chatId)
            .OrderBy(x => x.Date)
            .ToListAsync();

        if (list.Count == 0)
            return "История пока пустая.";

        var sym = GetCurrencySymbol(currency);

        var grouped = list
            .GroupBy(x => x.Date.Date)
            .OrderBy(x => x.Key)
            .Select(g => new
            {
                Date = g.Key,
                Salary = g.Where(x => x.Type == TransactionType.Salary).Sum(x => x.Amount),
                Payment = g.Where(x => x.Type == TransactionType.Payment).Sum(x => x.Amount),
                Ndfl = g.Where(x => x.Type == TransactionType.Vat).Sum(x => x.Amount)
            })
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Дата       |  Начисл  | Выплата  |   НДФЛ   |  Остаток ({sym})");
        sb.AppendLine("-----------+----------+----------+----------+----------");

        decimal balance = 0;
        foreach (var row in grouped)
        {
            balance += row.Salary + row.Ndfl - row.Payment;

            sb.Append(row.Date.ToString("dd.MM.yyyy")).Append(" | ")
                .Append(FormatCell(row.Salary)).Append(" | ")
                .Append(FormatCell(row.Payment)).Append(" | ")
                .Append(FormatCell(row.Ndfl)).Append(" | ")
                .Append(FormatCell(balance)).AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<string> GetHistoryMatrix(long chatId)
    {
        var list = await _db.Transactions
            .Where(x => x.ChatId == chatId)
            .OrderBy(x => x.Date)
            .ToListAsync();

        if (list.Count == 0)
            return "История пока пустая.";

        var grouped = list
            .GroupBy(x => x.Date.Date)
            .OrderBy(x => x.Key)
            .Select(g => new
            {
                Date = g.Key,
                Salary = g.Where(x => x.Type == TransactionType.Salary).Sum(x => x.Amount),
                Payment = g.Where(x => x.Type == TransactionType.Payment).Sum(x => x.Amount),
                Ndfl = g.Where(x => x.Type == TransactionType.Vat).Sum(x => x.Amount)
            })
            .ToList();

        var balanceByDate = new List<decimal>(grouped.Count);
        decimal runningBalance = 0m;
        foreach (var row in grouped)
        {
            runningBalance += row.Salary + row.Ndfl - row.Payment;
            balanceByDate.Add(runningBalance);
        }

        var numericValues = grouped
            .SelectMany(x => new[] { x.Salary, x.Payment, x.Ndfl })
            .Concat(balanceByDate)
            .Select(v => v.ToString("N0"))
            .ToList();

        var dateValues = grouped
            .Select(x => x.Date.ToString("dd.MM"))
            .ToList();

        var columnWidth = Math.Max(8, Math.Max(
            dateValues.Count == 0 ? 0 : dateValues.Max(x => x.Length),
            numericValues.Count == 0 ? 0 : numericValues.Max(x => x.Length)));

        const int rowLabelWidth = 10;
        var sb = new StringBuilder();

        AppendMatrixRow(sb, rowLabelWidth, columnWidth, "Период", dateValues);
        AppendSeparator(sb, rowLabelWidth, columnWidth, grouped.Count);
        AppendMatrixRow(sb, rowLabelWidth, columnWidth, "Зарплата", grouped.Select(x => x.Salary.ToString("N0")));
        AppendMatrixRow(sb, rowLabelWidth, columnWidth, "Получил", grouped.Select(x => x.Payment.ToString("N0")));
        AppendMatrixRow(sb, rowLabelWidth, columnWidth, "НДФЛ", grouped.Select(x => x.Ndfl.ToString("N0")));
        AppendMatrixRow(sb, rowLabelWidth, columnWidth, "Остаток", balanceByDate.Select(x => x.ToString("N0")));

        return sb.ToString().TrimEnd();
    }

    public async Task<string> UpdateAmountByDate(long chatId, DateTime date, decimal newAmount, bool editReceivedPayment = false, string currency = "RUB")
    {
        var normalizedDate = NormalizeToUtc(date);
        var typeToEdit = editReceivedPayment ? TransactionType.Payment : TransactionType.Salary;
        var typeLabel = editReceivedPayment ? "выплата" : "начисление";
        var sym = GetCurrencySymbol(currency);

        var items = await _db.Transactions
            .Where(x => x.ChatId == chatId)
            .Where(x => x.Type == typeToEdit)
            .Where(x => x.Date.Date == normalizedDate.Date)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Id)
            .ToListAsync();

        if (items.Count == 0)
            return $"{typeLabel} за {normalizedDate:dd.MM.yyyy} не найдена.";

        var target = items[^1];
        var oldAmount = target.Amount;
        target.Amount = newAmount;

        await _db.SaveChangesAsync();

        if (items.Count > 1)
        {
            return
$"""
В этот день найдено {items.Count} записей типа "{typeLabel}".
Изменена последняя запись: {oldAmount:N0} {sym} -> {newAmount:N0} {sym}.
""";
        }

        return $"{typeLabel} за {normalizedDate:dd.MM.yyyy} изменена: {oldAmount:N0} {sym} -> {newAmount:N0} {sym}.";
    }

    private static DateTime NormalizeToUtc(DateTime date)
    {
        return date.Kind switch
        {
            DateTimeKind.Utc => date,
            DateTimeKind.Local => date.ToUniversalTime(),
            _ => DateTime.SpecifyKind(date, DateTimeKind.Utc)
        };
    }

    private static string GetTypeLabel(TransactionType type)
    {
        return type switch
        {
            TransactionType.Salary => "Начисление",
            TransactionType.Payment => "Выплата",
            TransactionType.Vat => "НДФЛ",
            _ => type.ToString()
        };
    }

    private static string FormatCell(decimal value)
    {
        return value.ToString("N0").PadLeft(8);
    }

    private static void AppendMatrixRow(
        StringBuilder sb,
        int rowLabelWidth,
        int columnWidth,
        string label,
        IEnumerable<string> values)
    {
        sb.Append(label.PadRight(rowLabelWidth)).Append(" | ");
        sb.AppendLine(string.Join(" | ", values.Select(v => v.PadLeft(columnWidth))));
    }

    private static void AppendSeparator(
        StringBuilder sb,
        int rowLabelWidth,
        int columnWidth,
        int columns)
    {
        sb.Append(new string('-', rowLabelWidth)).Append("-+-");
        for (var i = 0; i < columns; i++)
        {
            if (i > 0)
                sb.Append("-+-");

            sb.Append(new string('-', columnWidth));
        }

        sb.AppendLine();
    }

    private async Task<DateTime?> GetCalculationStartDate(long chatId)
    {
        var settings = await _db.BotSettings
            .FirstOrDefaultAsync(x => x.ChatId == chatId);

        if (settings?.CalculationStartYear is null || settings.CalculationStartMonth is null)
            return null;

        return DateTime.SpecifyKind(
            new DateTime(
                settings.CalculationStartYear.Value,
                settings.CalculationStartMonth.Value,
                settings.CalculationStartDay ?? 1,
                0,
                0,
                0),
            DateTimeKind.Utc);
    }

    private async Task<bool> IsNdflEnabled(long chatId)
    {
        var settings = await _db.BotSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChatId == chatId);

        return settings?.IsNdflEnabled ?? true;
    }

    private async Task<DateTime?> GetNdflStartDate(long chatId)
    {
        var settings = await _db.BotSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChatId == chatId);

        if (settings?.NdflStartYear is null || settings.NdflStartMonth is null)
            return null;

        return DateTime.SpecifyKind(
            new DateTime(
                settings.NdflStartYear.Value,
                settings.NdflStartMonth.Value,
                settings.NdflStartDay ?? 1,
                0,
                0,
                0),
            DateTimeKind.Utc);
    }
}
