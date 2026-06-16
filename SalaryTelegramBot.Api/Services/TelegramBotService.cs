using System.Globalization;
using System.Net;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace SalaryTelegramBot.Api.Services;

public class TelegramBotService
{
    private static readonly string[] AcceptedDateFormats = ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd"];
    private const string CbStatus = "menu:status";
    private const string CbHistory = "menu:history";
    private const string CbPay = "menu:pay";
    private const string CbSettings = "menu:settings";
    private const string CbSchedule = "menu:schedule";
    private const string CbScheduleAdd = "menu:schedule_add";
    private const string CbScheduleDel = "menu:schedule_del";
    private const string CbScheduleTime = "menu:schedule_time";
    private const string CbCalcFrom = "menu:calcfrom";
    private const string CbRecalc = "menu:recalc";
    private const string CbNdflFlag = "menu:ndflflag";
    private const string CbNdflFrom = "menu:ndflfrom";
    private const string CbEditAmount = "menu:editamount";
    private const string CbMain = "menu:main";
    private const string CbCurrency = "menu:currency";
    private const string CbCurrencySet = "menu:currencysymbol";
    private static readonly (string Code, string Flag, string Name)[] Currencies =
        [("USD", "\U0001f1fa\U0001f1f8", "Доллар"), ("EUR", "\U0001f1ea\U0001f1fa", "Евро"), ("RUB", "\U0001f1f7\U0001f1fa", "Рос. рубль"), ("BYN", "\U0001f1e7\U0001f1fe", "Бел. рубль")];
    private readonly IServiceProvider _provider;
    private readonly IConfiguration _config;
    private readonly BotStateService _state;
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger<TelegramBotService> _logger;

    public TelegramBotService(
        IServiceProvider provider,
        IConfiguration config,
        BotStateService state,
        RateLimiter rateLimiter,
        ILogger<TelegramBotService> logger)
    {
        _provider = provider;
        _config = config;
        _state = state;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    private TelegramBotClient CreateBotClient() => new(
        _config["Telegram:Token"]
        ?? Environment.GetEnvironmentVariable("TELEGRAM__TOKEN")
        ?? Environment.GetEnvironmentVariable("TELEGRAM__Token")
        ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")!);

    private delegate Task OutputFunc(string text, IReplyMarkup? keyboard = null, ParseMode parseMode = ParseMode.None);

    public async Task HandleMessageAsync(long chatId, string text, CancellationToken token)
    {
        if (!_rateLimiter.IsAllowed(chatId))
            return;

        var bot = CreateBotClient();

        async Task SendMessage(string t, IReplyMarkup? k = null, ParseMode pm = ParseMode.None)
            => await bot.SendMessage(chatId, t, replyMarkup: k, parseMode: pm, cancellationToken: token);

        await ExecuteCommandAsync(chatId, text, SendMessage, token);
    }

    public async Task HandleCallbackAsync(long chatId, string data, string callbackQueryId, int messageId, CancellationToken token)
    {
        if (!_rateLimiter.IsAllowed(chatId))
            return;

        var bot = CreateBotClient();

        async Task EditMessage(string t, IReplyMarkup? k = null, ParseMode pm = ParseMode.None)
        {
            try
            {
                if (k is InlineKeyboardMarkup ikm)
                    await bot.EditMessageText(chatId, messageId, t,
                        replyMarkup: ikm, parseMode: pm, cancellationToken: token);
                else
                    await bot.SendMessage(chatId, t, replyMarkup: k, parseMode: pm, cancellationToken: token);
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to edit message, sending new one");
                await bot.SendMessage(chatId, t, replyMarkup: k, parseMode: pm, cancellationToken: token);
            }
        }

        await ExecuteCommandAsync(chatId, data, EditMessage, token);
    }

    private async Task ExecuteCommandAsync(long chatId, string text, OutputFunc output, CancellationToken token)
    {
        using var scope = _provider.CreateScope();
        var salaryService = scope.ServiceProvider.GetRequiredService<SalaryService>();
        var scheduleService = scope.ServiceProvider.GetRequiredService<SalaryScheduleService>();

        await scheduleService.EnsureSeededForChatAsync(chatId);

        if (text == "Отмена")
        {
            _state.ClearAll(chatId);
            await output("Действие отменено.", GetMainKeyboard());
            return;
        }

        var currentState = _state.GetState(chatId);
        if (currentState is not null)
        {
            var parsed = Enum.Parse<BotAwaitState>(currentState);
            var handled = parsed switch
            {
                BotAwaitState.PayAmountInput => await HandlePayAmountInput(chatId, text, output),
                BotAwaitState.PayDateInput => await HandlePayDateInput(salaryService, chatId, text, output),
                BotAwaitState.ScheduleAddInput => await HandleScheduleAddInput(scheduleService, chatId, text, output),
                BotAwaitState.ScheduleDelInput => await HandleScheduleDeleteInput(scheduleService, chatId, text, output),
                BotAwaitState.ScheduleTimeInput => await HandleScheduleTimeInput(scheduleService, chatId, text, output),
                BotAwaitState.CalculationMonthInput => await HandleCalculationMonthInput(scheduleService, chatId, text, output),
                BotAwaitState.NdflFromInput => await HandleNdflFromInput(scheduleService, chatId, text, output),
                BotAwaitState.EditPayInput => await HandleEditPayInput(salaryService, chatId, text, output),
                _ => false
            };

            if (handled)
                _state.RemoveState(chatId);

            return;
        }

        if (text is "/start" or "/help")
        {
            await output("Этот бот помогает учитывать общий долг по зарплате:\nначисления и запрошенные частичные выплаты.\n\nВыберите действие в меню ниже.",
                await GetMainKeyboardAsync(scheduleService, chatId));
            return;
        }

        if (text == "💸 Выплата")
        {
            _state.SetState(chatId, nameof(BotAwaitState.PayAmountInput));
            await output("💸 Учет полученной выплаты\n\nШаг 1: введите сумму.", GetCancelKeyboard());
            return;
        }

        if (text == "➕ Начисление")
        {
            _state.SetState(chatId, nameof(BotAwaitState.ScheduleAddInput));
            await output("➕ Добавление начисления\n\nВведите: <день> <сумма>", GetCancelKeyboard());
            return;
        }

        if (text == "➖ Начисление")
        {
            _state.SetState(chatId, nameof(BotAwaitState.ScheduleDelInput));
            await output("➖ Удаление начисления\n\nВведите день месяца.", GetCancelKeyboard());
            return;
        }

        if (text == "⏰ Время начисления")
        {
            _state.SetState(chatId, nameof(BotAwaitState.ScheduleTimeInput));
            await output("⏰ Время автопроверки\n\nВведите: ЧЧ:ММ", GetCancelKeyboard());
            return;
        }

        if (text == "/calcfrom")
        {
            _state.SetState(chatId, nameof(BotAwaitState.CalculationMonthInput));
            await output("📅 Дата начала расчета\n\nВведите: ДД.ММ.ГГГГ или ММ.ГГГГ", GetCancelKeyboard());
            return;
        }

        if (text.StartsWith("/calcfrom ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var arg = text["/calcfrom ".Length..].Trim();
                var (day, month, year) = ParseCalculationDate(arg);
                var result = await scheduleService.SetCalculationStartDateAsync(chatId, day, month, year);
                await output(result, GetMainKeyboard());
            }
            catch
            {
                await output("Формат: /calcfrom 15.11.2025");
            }
            return;
        }

        if (text == "/status")
        {
            var cur = await scheduleService.GetCurrencyAsync(chatId);
            var result = await salaryService.GetStatus(chatId, cur);
            await output(result);
            return;
        }

        if (text == "/users")
        {
            var adminIds = _config.GetSection("Telegram:AdminUserIds").Get<long[]>() ?? [];
            if (!adminIds.Contains(chatId))
            {
                await output("Нет доступа.");
                return;
            }
            var count = await salaryService.GetUserCount();
            await output($"Пользователей: {count}");
            return;
        }

        if (text == "/recalc")
        {
            var result = await scheduleService.RecalculateAccrualsAsync(chatId, salaryService);
            await output(result);
            return;
        }

        if (text == "/ndflflag")
        {
            var result = await scheduleService.ToggleNdflAsync(chatId);
            await output(result);
            return;
        }

        if (text == "/ndflfrom")
        {
            _state.SetState(chatId, nameof(BotAwaitState.NdflFromInput));
            await output("📌 Дата начала НДФЛ\n\nВведите: ДД.ММ.ГГГГ", GetCancelKeyboard());
            return;
        }

        if (text.StartsWith("/ndflfrom ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var arg = text["/ndflfrom ".Length..].Trim();
                var (day, month, year) = ParseCalculationDate(arg);
                var result = await scheduleService.SetNdflStartDateAsync(chatId, day, month, year);
                await output(result, GetMainKeyboard());
            }
            catch
            {
                await output("Формат: /ndflfrom 01.01.2026");
            }
            return;
        }

        if (text == "/history")
        {
            var cur = await scheduleService.GetCurrencyAsync(chatId);
            var result = await salaryService.GetHistory(chatId, cur);
            await output($"<pre>{WebUtility.HtmlEncode(result)}</pre>", parseMode: ParseMode.Html);
            return;
        }

        if (text == "/editamount" || text == "/editpay")
        {
            _state.SetState(chatId, nameof(BotAwaitState.EditPayInput));
            await output("✏️ Формат: <дата> <сумма> [получил]", GetCancelKeyboard());
            return;
        }

        if (text.StartsWith("/editamount ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleEditPayInput(salaryService, chatId, text["/editamount ".Length..], output);
            return;
        }

        if (text.StartsWith("/editpay ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleEditPayInput(salaryService, chatId, text["/editpay ".Length..], output);
            return;
        }

        if (text.StartsWith("/schedule"))
        {
            await HandleScheduleCommand(chatId, scheduleService, text, output);
            return;
        }

        if (text.StartsWith("/pay"))
        {
            try
            {
                var split = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                decimal amount = decimal.Parse(split[1], CultureInfo.InvariantCulture);
                DateTime date = split.Length >= 3 ? ParseUserDate(split[2]) : DateTime.Now;
                await salaryService.AddPayment(chatId, amount, date);
                await output("Полученная выплата сохранена");
            }
            catch
            {
                await output("Ошибка команды");
            }
            return;
        }

        var callbackData = text switch
        {
            "📊 Статус" => CbStatus,
            "📜 История" => CbHistory,
            "🗓️ Расписание" => CbSchedule,
            "📅 Дата расчета" => CbCalcFrom,
            "🔄 Пересчитать" => CbRecalc,
            "🏷️ Флаг НДФЛ" => CbNdflFlag,
            "📌 Дата НДФЛ" => CbNdflFrom,
            "✏️ Изменить сумму" => CbEditAmount,
            _ => null
        };

        if (callbackData is not null)
        {
            await ExecuteCommandAsync(chatId, callbackData, output, token);
            return;
        }

        await HandleCallbackDataAsync(chatId, text, output, scheduleService, salaryService);
    }

    private async Task HandleCallbackDataAsync(long chatId, string data, OutputFunc output,
        SalaryScheduleService scheduleService, SalaryService salaryService)
    {
        switch (data)
        {
            case CbMain:
                await output("Выберите действие:", await GetMainKeyboardAsync(scheduleService, chatId));
                break;
            case CbStatus:
                var statusCur = await scheduleService.GetCurrencyAsync(chatId);
                var status = await salaryService.GetStatus(chatId, statusCur);
                await output(status, await GetMainKeyboardAsync(scheduleService, chatId));
                break;
            case CbHistory:
                var histCur = await scheduleService.GetCurrencyAsync(chatId);
                var history = await salaryService.GetHistory(chatId, histCur);
                await output($"<pre>{WebUtility.HtmlEncode(history)}</pre>",
                    await GetMainKeyboardAsync(scheduleService, chatId), ParseMode.Html);
                break;
            case CbPay:
                _state.SetState(chatId, nameof(BotAwaitState.PayAmountInput));
                await output("💸 Введите сумму выплаты:", GetBackKeyboard(CbMain));
                break;
            case CbSettings:
                var settingsText = await BuildSettingsSummaryAsync(scheduleService, chatId);
                await output(settingsText, await GetSettingsKeyboardAsync(scheduleService, chatId));
                break;
            case CbSchedule:
                var schedule = await scheduleService.FormatScheduleAsync(chatId);
                await output(schedule, await GetSettingsKeyboardAsync(scheduleService, chatId));
                break;
            case CbScheduleAdd:
                _state.SetState(chatId, nameof(BotAwaitState.ScheduleAddInput));
                await output("➕ Введите: <день> <сумма>", GetBackKeyboard(CbSettings));
                break;
            case CbScheduleDel:
                _state.SetState(chatId, nameof(BotAwaitState.ScheduleDelInput));
                await output("➖ Введите день месяца:", GetBackKeyboard(CbSettings));
                break;
            case CbScheduleTime:
                _state.SetState(chatId, nameof(BotAwaitState.ScheduleTimeInput));
                await output("⏰ Введите время (ЧЧ:ММ):", GetBackKeyboard(CbSettings));
                break;
            case CbCalcFrom:
                _state.SetState(chatId, nameof(BotAwaitState.CalculationMonthInput));
                await output("📅 Введите дату (ДД.ММ.ГГГГ):", GetBackKeyboard(CbSettings));
                break;
            case CbRecalc:
                var recalc = await scheduleService.RecalculateAccrualsAsync(chatId, salaryService);
                await output(recalc, await GetSettingsKeyboardAsync(scheduleService, chatId));
                break;
            case CbNdflFlag:
                var ndfl = await scheduleService.ToggleNdflAsync(chatId);
                await output(ndfl, await GetSettingsKeyboardAsync(scheduleService, chatId));
                break;
            case CbNdflFrom:
                _state.SetState(chatId, nameof(BotAwaitState.NdflFromInput));
                await output("📌 Введите дату старта НДФЛ:", GetBackKeyboard(CbSettings));
                break;
            case CbEditAmount:
                _state.SetState(chatId, nameof(BotAwaitState.EditPayInput));
                await output("✏️ Формат: <дата> <сумма> [получил]", GetBackKeyboard(CbSettings));
                break;
            case CbCurrency:
                var currentCurrency = await scheduleService.GetCurrencyAsync(chatId);
                var currButtons = Currencies.Select(c =>
                    c.Code == currentCurrency
                        ? InlineKeyboardButton.WithCallbackData($"{c.Flag} {c.Name} ✓", $"{CbCurrencySet}:{c.Code}")
                        : InlineKeyboardButton.WithCallbackData($"{c.Flag} {c.Name}", $"{CbCurrencySet}:{c.Code}")
                ).ToList();
                await output("Выберите валюту:", new InlineKeyboardMarkup([currButtons, [InlineKeyboardButton.WithCallbackData("⬅️ Назад", CbMain)]]));
                break;
            default:
                if (data.StartsWith(CbCurrencySet + ":"))
                {
                    var code = data[(CbCurrencySet.Length + 1)..];
                    using var rateScope = _provider.CreateScope();
                    var rateService = rateScope.ServiceProvider.GetRequiredService<NbrbRateService>();
                    var (msg, _) = await scheduleService.SetCurrencyAsync(chatId, code, rateService);
                    var newFlag = Currencies.FirstOrDefault(c => c.Code == code).Flag ?? "";
                    await output($"{newFlag} {msg}", await GetMainKeyboardAsync(scheduleService, chatId));
                }
                break;
        }
    }

    private static async Task<string> BuildSettingsSummaryAsync(SalaryScheduleService scheduleService, long chatId)
    {
        var (hour, minute) = await scheduleService.GetCheckTimeAsync(chatId);
        var calculationStart = await scheduleService.GetCalculationStartDateAsync(chatId);
        var ndflStart = await scheduleService.GetNdflStartDateAsync(chatId);
        var ndflEnabled = await scheduleService.IsNdflEnabledAsync(chatId);

        return
$"""
⚙️ Настройки

Здесь настраиваются правила, которые влияют на итоговый долг:
- даты и суммы плановых начислений;
- дата начала расчета;
- параметры НДФЛ;
- время ежедневной автопроверки.

Текущее состояние:
• Время проверки: {hour:D2}:{minute:D2}
• Дата начала расчета: {(calculationStart is null ? "за все время" : calculationStart.Value.ToString("dd.MM.yyyy"))}
• НДФЛ: {(ndflEnabled ? "включен" : "выключен")}
• Дата старта НДФЛ: {(ndflStart is null ? "с первого начисления" : ndflStart.Value.ToString("dd.MM.yyyy"))}
""";
    }

    private async Task HandleScheduleCommand(
        long chatId,
        SalaryScheduleService scheduleService,
        string text,
        OutputFunc output)
    {
        if (text == "/schedule")
        {
            var result = await scheduleService.FormatScheduleAsync(chatId);
            await output(result);
            return;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            await output("Неизвестная подкоманда.");
            return;
        }

        var sub = parts[1].ToLowerInvariant();

        try
        {
            var resultMessage = sub switch
            {
                "add" when parts.Length >= 4 => await scheduleService.AddOrUpdateRuleAsync(
                    chatId,
                    int.Parse(parts[2]),
                    decimal.Parse(parts[3], CultureInfo.InvariantCulture)),
                "del" or "remove" when parts.Length >= 3 => await scheduleService.RemoveRuleAsync(
                    chatId,
                    int.Parse(parts[2])),
                "time" when parts.Length >= 3 => await SetTimeFromCommand(scheduleService, chatId, parts[2]),
                _ => "Формат:\n/schedule add 23 150000\n/schedule del 23\n/schedule time 12:00"
            };
            await output(resultMessage);
        }
        catch
        {
            await output("Ошибка команды. Проверьте формат.");
        }
    }

    private static async Task<string> SetTimeFromCommand(
        SalaryScheduleService scheduleService,
        long chatId,
        string timeArg)
    {
        int hour;
        int minute;

        if (timeArg.Contains(':'))
        {
            var timeParts = timeArg.Split(':');
            hour = int.Parse(timeParts[0]);
            minute = timeParts.Length > 1 ? int.Parse(timeParts[1]) : 0;
        }
        else
        {
            hour = int.Parse(timeArg);
            minute = 0;
        }

        return await scheduleService.SetCheckTimeAsync(chatId, hour, minute);
    }

    private async Task<bool> HandlePayAmountInput(long chatId, string text, OutputFunc output)
    {
        try
        {
            var amount = decimal.Parse(text, CultureInfo.InvariantCulture);
            _state.SetPendingAmount(chatId, amount);
            _state.SetState(chatId, nameof(BotAwaitState.PayDateInput));

            await output("Шаг 2: введите дату выплаты.\n\nФорматы:\n- ДД.ММ.ГГГГ (например: 30.11.2025)\n- YYYY-MM-DD (например: 2025-11-30)\n- или напишите: сегодня",
                GetCancelKeyboard());
            return false;
        }
        catch
        {
            await output("Не смог распознать сумму. Введите только число.\n\nПримеры:\n100000\n75500",
                GetCancelKeyboard());
            return false;
        }
    }

    private async Task<bool> HandlePayDateInput(SalaryService salaryService, long chatId, string text, OutputFunc output)
    {
        var amount = _state.GetPendingAmount(chatId);
        if (amount is null)
        {
            _state.RemoveState(chatId);
            await output("Сумма не найдена. Нажмите 'Выплата' снова.", GetMainKeyboard());
            return true;
        }

        try
        {
            DateTime date = text.Equals("сегодня", StringComparison.OrdinalIgnoreCase)
                ? DateTime.Now
                : ParseUserDate(text);

            await salaryService.AddPayment(chatId, amount.Value, date);
            _state.RemovePendingAmount(chatId);
            await output("Полученная выплата сохранена", GetMainKeyboard());
            return true;
        }
        catch
        {
            await output("Не смог распознать дату.\n\nПримеры:\n30.11.2025\n2025-11-30\nсегодня", GetCancelKeyboard());
            return false;
        }
    }

    private async Task<bool> HandleScheduleAddInput(SalaryScheduleService scheduleService, long chatId, string text, OutputFunc output)
    {
        try
        {
            var split = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var day = int.Parse(split[0], CultureInfo.InvariantCulture);
            var amount = decimal.Parse(split[1], CultureInfo.InvariantCulture);

            var message = await scheduleService.AddOrUpdateRuleAsync(chatId, day, amount);
            await output(message, GetMainKeyboard());
            return true;
        }
        catch
        {
            await output("Не смог распознать ввод.\n\nНужен формат: <день_месяца> <сумма>\n\nПример:\n23 150000", GetCancelKeyboard());
            return false;
        }
    }

    private async Task<bool> HandleScheduleDeleteInput(SalaryScheduleService scheduleService, long chatId, string text, OutputFunc output)
    {
        try
        {
            var day = int.Parse(text, CultureInfo.InvariantCulture);
            var message = await scheduleService.RemoveRuleAsync(chatId, day);
            await output(message, GetMainKeyboard());
            return true;
        }
        catch
        {
            await output("Не смог распознать ввод.\n\nВведите только день месяца (число от 1 до 31).\n\nПример:\n23", GetCancelKeyboard());
            return false;
        }
    }

    private async Task<bool> HandleScheduleTimeInput(SalaryScheduleService scheduleService, long chatId, string text, OutputFunc output)
    {
        try
        {
            var message = await SetTimeFromCommand(scheduleService, chatId, text);
            await output(message, GetMainKeyboard());
            return true;
        }
        catch
        {
            await output("Не смог распознать время.\n\nФормат: ЧЧ:ММ (24 часа)\nПримеры:\n12:00\n00:05", GetCancelKeyboard());
            return false;
        }
    }

    private async Task<bool> HandleCalculationMonthInput(SalaryScheduleService scheduleService, long chatId, string text, OutputFunc output)
    {
        try
        {
            var (day, month, year) = ParseCalculationDate(text);
            var result = await scheduleService.SetCalculationStartDateAsync(chatId, day, month, year);
            await output(result, GetMainKeyboard());
            return true;
        }
        catch
        {
            await output("Не смог распознать дату.\n\nМожно так:\n15.11.2025\n11.2025", GetCancelKeyboard());
            return false;
        }
    }

    private async Task<bool> HandleNdflFromInput(SalaryScheduleService scheduleService, long chatId, string text, OutputFunc output)
    {
        try
        {
            var (day, month, year) = ParseCalculationDate(text);
            var result = await scheduleService.SetNdflStartDateAsync(chatId, day, month, year);
            await output(result, GetMainKeyboard());
            return true;
        }
        catch
        {
            await output("Не смог распознать дату старта НДФЛ.\n\nМожно так:\n01.01.2026\n01.2026", GetCancelKeyboard());
            return false;
        }
    }

    private async Task<bool> HandleEditPayInput(SalaryService salaryService, long chatId, string text, OutputFunc output)
    {
        try
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                throw new FormatException("Invalid format");

            var date = ParseUserDate(parts[0]);
            var amount = decimal.Parse(parts[1], CultureInfo.InvariantCulture);
            var editReceivedPayment = parts.Length >= 3 &&
                                      parts[2].Equals("получил", StringComparison.OrdinalIgnoreCase);
            var result = await salaryService.UpdateAmountByDate(chatId, date, amount, editReceivedPayment);
            await output(result, GetMainKeyboard());
            return true;
        }
        catch
        {
            await output("Не смог распознать ввод.\n\nФормат:\n30.11.2025 85000\n30.11.2025 85000 получил", GetCancelKeyboard());
            return false;
        }
    }

    private static (int Day, int Month, int Year) ParseCalculationDate(string text)
    {
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            2 => (1,
                int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture)),
            3 => (
                int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture)),
            _ => throw new FormatException("Invalid format")
        };
    }

    private static DateTime ParseUserDate(string text)
    {
        if (DateTime.TryParseExact(text, AcceptedDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            return parsed;
        throw new FormatException("Invalid date format");
    }

    private static InlineKeyboardMarkup GetMainKeyboard() => GetMainKeyboardWithCurrency("RUB");

    private static InlineKeyboardMarkup GetMainKeyboardWithCurrency(string currency = "RUB")
    {
        var flag = Currencies.FirstOrDefault(c => c.Code == currency).Flag ?? "\U0001f1f7\U0001f1fa";
        return new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("💰 Общий долг", CbStatus), InlineKeyboardButton.WithCallbackData("📚 История долга", CbHistory)],
            [InlineKeyboardButton.WithCallbackData("💸 Учет полученной выплаты", CbPay)],
            [InlineKeyboardButton.WithCallbackData("⚙️ Настройки", CbSettings)],
            [InlineKeyboardButton.WithCallbackData($"{flag} Валюта: {currency}", CbCurrency)]
        ]);
    }

    private async Task<InlineKeyboardMarkup> GetMainKeyboardAsync(SalaryScheduleService scheduleService, long chatId)
    {
        var currency = await scheduleService.GetCurrencyAsync(chatId);
        return GetMainKeyboardWithCurrency(currency);
    }

    private async Task<InlineKeyboardMarkup> GetSettingsKeyboardAsync(SalaryScheduleService scheduleService, long chatId)
    {
        var ndflEnabled = await scheduleService.IsNdflEnabledAsync(chatId);
        var ndflLabel = $"🏷️ НДФЛ: {(ndflEnabled ? "вкл" : "выкл")}";

        return new InlineKeyboardMarkup([
            [InlineKeyboardButton.WithCallbackData("📆 Правила начислений", CbSchedule)],
            [InlineKeyboardButton.WithCallbackData("➕ Добавить правило", CbScheduleAdd), InlineKeyboardButton.WithCallbackData("➖ Удалить правило", CbScheduleDel)],
            [InlineKeyboardButton.WithCallbackData("🕒 Время проверки", CbScheduleTime), InlineKeyboardButton.WithCallbackData("📅 Дата начала расчета", CbCalcFrom)],
            [InlineKeyboardButton.WithCallbackData(ndflLabel, CbNdflFlag), InlineKeyboardButton.WithCallbackData("📌 Дата старта НДФЛ", CbNdflFrom)],
            [InlineKeyboardButton.WithCallbackData("✏️ Изменить сумму записи", CbEditAmount), InlineKeyboardButton.WithCallbackData("🔄 Пересчитать начисления", CbRecalc)],
            [InlineKeyboardButton.WithCallbackData("⬅️ Назад", CbMain)]
        ]);
    }

    private static InlineKeyboardMarkup GetBackKeyboard(string backCallbackData) =>
        new InlineKeyboardMarkup([[InlineKeyboardButton.WithCallbackData("⬅️ Назад", backCallbackData)]]);

    private static ReplyKeyboardMarkup GetCancelKeyboard() =>
        new ReplyKeyboardMarkup([[new KeyboardButton("Отмена")]]) { ResizeKeyboard = true };

    private enum BotAwaitState
    {
        PayAmountInput, PayDateInput, ScheduleAddInput, ScheduleDelInput,
        ScheduleTimeInput, CalculationMonthInput, NdflFromInput, EditPayInput
    }
}
