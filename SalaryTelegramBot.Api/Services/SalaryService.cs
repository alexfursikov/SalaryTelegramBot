using System.Collections.Concurrent;
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
    private readonly EncryptionService _encryption;
    private readonly UserKeyCache _keyCache;
    private static readonly ConcurrentDictionary<long, Models.BotSettings> _settingsCache = new();

    public SalaryService(
        AppDbContext db,
        IOptions<SalarySettings> salaryOptions,
        EncryptionService encryption,
        UserKeyCache keyCache)
    {
        _db = db;
        _salarySettings = salaryOptions.Value;
        _encryption = encryption;
        _keyCache = keyCache;
    }

    public async Task AddPayment(long chatId, decimal amount, DateTime date)
    {
        var normalizedDate = NormalizeToUtc(date);
        var key = _keyCache.GetKey(chatId);

        var tx = new Transaction
        {
            ChatId = chatId,
            Amount = amount,
            Date = normalizedDate,
            Type = TransactionType.Payment,
            EncryptedAmount = key is not null ? _encryption.Encrypt(amount, key) : null
        };

        _db.Transactions.Add(tx);
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

        var ndflAmount = Math.Round((amount / (1m - ndflRate)) - amount, 2, MidpointRounding.AwayFromZero);
        var key = _keyCache.GetKey(chatId);

        _db.Transactions.Add(new Transaction
        {
            ChatId = chatId,
            Amount = amount,
            Date = normalizedDate,
            Type = TransactionType.Salary,
            EncryptedAmount = key is not null ? _encryption.Encrypt(amount, key) : null
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
                Comment = "auto_ndfl",
                EncryptedAmount = key is not null ? _encryption.Encrypt(ndflAmount, key) : null
            });
        }

        await _db.SaveChangesAsync();
    }

    public async Task AddSalaryEncrypted(long chatId, byte[] encryptedAmount, DateTime date)
    {
        var normalizedDate = NormalizeToUtc(date);

        bool exists = await _db.Transactions
            .AnyAsync(x =>
                x.ChatId == chatId &&
                x.Date.Date == normalizedDate.Date &&
                x.Type == TransactionType.Salary);

        if (exists)
            return;

        _db.Transactions.Add(new Transaction
        {
            ChatId = chatId,
            Amount = 0,
            Date = normalizedDate,
            Type = TransactionType.Salary,
            EncryptedAmount = encryptedAmount
        });

        await _db.SaveChangesAsync();
    }

    public async Task<int> RecalculateNdflForRange(long chatId, DateTime fromDate, DateTime toDate)
    {
        var key = _keyCache.GetKey(chatId);
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

        if (key is not null)
            DecryptTransactions(salaries, key);

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
                    Comment = "auto_ndfl",
                    EncryptedAmount = key is not null ? _encryption.Encrypt(expected, key) : null
                });
                changed++;
                continue;
            }

            var first = vatList[0];
            if (key is not null && first.EncryptedAmount is not null)
                first.Amount = _encryption.Decrypt(first.EncryptedAmount, key);

            if (first.Amount != expected || first.Date != salaryTx.Date)
            {
                first.Amount = expected;
                first.Date = salaryTx.Date;
                if (key is not null)
                    first.EncryptedAmount = _encryption.Encrypt(expected, key);
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
        var key = _keyCache.GetKey(chatId);
        var calculationStartDate = await GetCalculationStartDate(chatId);

        var transactions = await _db.Transactions
            .Where(x => x.ChatId == chatId)
            .Where(x => calculationStartDate == null || x.Date >= calculationStartDate.Value)
            .ToListAsync();

        if (key is not null)
            DecryptTransactions(transactions, key);

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
        var key = _keyCache.GetKey(chatId);

        var list = await _db.Transactions
            .Where(x => x.ChatId == chatId)
            .OrderBy(x => x.Date)
            .ToListAsync();

        if (key is not null)
            DecryptTransactions(list, key);

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
        var key = _keyCache.GetKey(chatId);

        var list = await _db.Transactions
            .Where(x => x.ChatId == chatId)
            .OrderBy(x => x.Date)
            .ToListAsync();

        if (key is not null)
            DecryptTransactions(list, key);

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
        var key = _keyCache.GetKey(chatId);
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

        if (key is not null && target.EncryptedAmount is not null)
            target.Amount = _encryption.Decrypt(target.EncryptedAmount, key);

        var oldAmount = target.Amount;
        target.Amount = newAmount;
        if (key is not null)
            target.EncryptedAmount = _encryption.Encrypt(newAmount, key);

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

    public async Task<int> EncryptUserDataAsync(long chatId, byte[] key)
    {
        var connStr = _db.Database.GetConnectionString();
        if (!connStr.Contains("PrepareThreshold"))
            connStr += ";PrepareThreshold=0";
        await using var conn = new Npgsql.NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var amounts = new Dictionary<int, decimal>();
        await using (var cmd = new Npgsql.NpgsqlCommand("SELECT \"Id\", \"Amount\" FROM \"Transactions\" WHERE \"ChatId\" = $1 AND \"EncryptedAmount\" IS NULL", conn))
        {
            cmd.Parameters.Add(new Npgsql.NpgsqlParameter("$1", chatId));
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                amounts[reader.GetInt32(0)] = reader.GetDecimal(1);
        }

        foreach (var (id, amount) in amounts)
        {
            var enc = _encryption.Encrypt(amount, key);
            await using var cmd = new Npgsql.NpgsqlCommand("UPDATE \"Transactions\" SET \"EncryptedAmount\" = $1 WHERE \"Id\" = $2", conn);
            cmd.Parameters.Add(new Npgsql.NpgsqlParameter("$1", enc));
            cmd.Parameters.Add(new Npgsql.NpgsqlParameter("$2", id));
            await cmd.ExecuteNonQueryAsync();
        }

        var ruleAmounts = new Dictionary<int, decimal>();
        await using (var cmd2 = new Npgsql.NpgsqlCommand("SELECT \"Id\", \"Amount\" FROM \"AccrualRules\" WHERE \"ChatId\" = $1 AND \"EncryptedAmount\" IS NULL", conn))
        {
            cmd2.Parameters.Add(new Npgsql.NpgsqlParameter("$1", chatId));
            await using var reader2 = await cmd2.ExecuteReaderAsync();
            while (await reader2.ReadAsync())
                ruleAmounts[reader2.GetInt32(0)] = reader2.GetDecimal(1);
        }

        foreach (var (id, amount) in ruleAmounts)
        {
            var enc = _encryption.Encrypt(amount, key);
            await using var cmd = new Npgsql.NpgsqlCommand("UPDATE \"AccrualRules\" SET \"EncryptedAmount\" = $1 WHERE \"Id\" = $2", conn);
            cmd.Parameters.Add(new Npgsql.NpgsqlParameter("$1", enc));
            cmd.Parameters.Add(new Npgsql.NpgsqlParameter("$2", id));
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmdZero = new Npgsql.NpgsqlCommand("UPDATE \"Transactions\" SET \"Amount\" = 0 WHERE \"ChatId\" = $1 AND \"EncryptedAmount\" IS NOT NULL", conn))
        {
            cmdZero.Parameters.Add(new Npgsql.NpgsqlParameter("$1", chatId));
            await cmdZero.ExecuteNonQueryAsync();
        }

        await using (var cmdZero2 = new Npgsql.NpgsqlCommand("UPDATE \"AccrualRules\" SET \"Amount\" = 0 WHERE \"ChatId\" = $1 AND \"EncryptedAmount\" IS NOT NULL", conn))
        {
            cmdZero2.Parameters.Add(new Npgsql.NpgsqlParameter("$1", chatId));
            await cmdZero2.ExecuteNonQueryAsync();
        }

        var settings = await _db.BotSettings.FirstOrDefaultAsync(x => x.ChatId == chatId);
        if (settings is not null)
        {
            settings.IsEncrypted = true;
            await _db.SaveChangesAsync();
        }

        return amounts.Count + ruleAmounts.Count;
    }

    private void DecryptTransactions(List<Transaction> transactions, byte[] key)
    {
        foreach (var tx in transactions)
        {
            if (tx.EncryptedAmount is not null)
                tx.Amount = _encryption.Decrypt(tx.EncryptedAmount, key);
        }
    }

    public void DecryptAccrualRules(List<AccrualRule> rules, byte[] key)
    {
        foreach (var rule in rules)
        {
            if (rule.EncryptedAmount is not null)
                rule.Amount = _encryption.Decrypt(rule.EncryptedAmount, key);
        }
    }

    public void DecryptAccrualRule(AccrualRule rule, byte[] key)
    {
        if (rule.EncryptedAmount is not null)
            rule.Amount = _encryption.Decrypt(rule.EncryptedAmount, key);
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

    private async Task<Models.BotSettings> GetSettingsCachedAsync(long chatId)
    {
        if (_settingsCache.TryGetValue(chatId, out var cached))
            return cached;

        var settings = await _db.BotSettings
            .FirstOrDefaultAsync(x => x.ChatId == chatId);

        if (settings is not null)
            _settingsCache[chatId] = settings;

        return settings;
    }

    private async Task<DateTime?> GetCalculationStartDate(long chatId)
    {
        var settings = await GetSettingsCachedAsync(chatId);

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
        var settings = await GetSettingsCachedAsync(chatId);
        return settings?.IsNdflEnabled ?? true;
    }

    private async Task<DateTime?> GetNdflStartDate(long chatId)
    {
        var settings = await GetSettingsCachedAsync(chatId);

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
