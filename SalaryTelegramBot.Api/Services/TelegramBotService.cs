using System.Globalization;
using System.Net;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace SalaryTelegramBot.Api.Services;

public class TelegramBotService : BackgroundService
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

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var token = _config["Telegram:Token"];

        if (string.IsNullOrWhiteSpace(token))
            token = Environment.GetEnvironmentVariable("TELEGRAM__TOKEN")
                 ?? Environment.GetEnvironmentVariable("TELEGRAM__Token")
                 ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("ERROR: Telegram bot token not configured. Set TELEGRAM__TOKEN env var.");
            return;
        }

        Console.WriteLine($"Bot token: {token[..Math.Min(5, token.Length)]}...{token[^4..]}");

        var bot = new TelegramBotClient(token);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        bot.StartReceiving(
            HandleUpdate,
            HandleError,
            receiverOptions,
            stoppingToken);

        Console.WriteLine("Bot started");

        await Task.Delay(-1, stoppingToken);
    }

    private async Task HandleUpdate(
        ITelegramBotClient bot,
        Update update,
        CancellationToken token)
    {
        if (update.CallbackQuery is { } callbackQuery)
        {
            var cbChatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
            if (!_rateLimiter.IsAllowed(cbChatId))
                return;

            using var cbScope = _provider.CreateScope();
            var cbSalaryService = cbScope.ServiceProvider.GetRequiredService<SalaryService>();
            var cbScheduleService = cbScope.ServiceProvider.GetRequiredService<SalaryScheduleService>();
            await HandleCallbackQuery(bot, callbackQuery, cbSalaryService, cbScheduleService, token);
            return;
        }

        if (update.Message?.Text is null)
            return;

        var text = update.Message.Text.Trim();
        var chatId = update.Message.Chat.Id;

        if (!_rateLimiter.IsAllowed(chatId))
            return;

        using var scope = _provider.CreateScope();

        var salaryService =
            scope.ServiceProvider.GetRequiredService<SalaryService>();

        var scheduleService =
            scope.ServiceProvider.GetRequiredService<SalaryScheduleService>();
        
        await scheduleService.EnsureSeededForChatAsync(chatId);

        if (text == "Отмена")
        {
            _state.ClearAll(chatId);
            await bot.SendMessage(chatId, "Действие отменено.", replyMarkup: GetMainKeyboard(), cancellationToken: token);
            return;
        }

        var currentState = _state.GetState(chatId);
        if (currentState is not null)
        {
            var parsed = Enum.Parse<BotAwaitState>(currentState);
            var handled = parsed switch
            {
                BotAwaitState.PayAmountInput => await HandlePayAmountInput(bot, chatId, text, token),
                BotAwaitState.PayDateInput => await HandlePayDateInput(bot, salaryService, chatId, text, token),
                BotAwaitState.ScheduleAddInput => await HandleScheduleAddInput(bot, scheduleService, chatId, text, token),
                BotAwaitState.ScheduleDelInput => await HandleScheduleDeleteInput(bot, scheduleService, chatId, text, token),
                BotAwaitState.ScheduleTimeInput => await HandleScheduleTimeInput(bot, scheduleService, chatId, text, token),
                BotAwaitState.CalculationMonthInput => await HandleCalculationMonthInput(bot, scheduleService, chatId, text, token),
                BotAwaitState.NdflFromInput => await HandleNdflFromInput(bot, scheduleService, chatId, text, token),
                BotAwaitState.EditPayInput => await HandleEditPayInput(bot, salaryService, chatId, text, token),
                _ => false
            };

            if (handled)
                _state.RemoveState(chatId);

            return;
        }

        if (text is "/start" or "/help")
        {
            await bot.SendMessage(
                chatId,
"""
Этот бот помогает учитывать общий долг по зарплате:
начисления и запрошенные частичные выплаты.

Выберите действие в меню ниже.
""",
                replyMarkup: await GetMainKeyboardAsync(scheduleService, chatId),
                cancellationToken: token);

            return;
        }

        if (text == "ℹ️ Помощь")
            text = "/help";
        else if (text == "📊 Статус")
            text = "/status";
        else if (text == "📜 История")
            text = "/history";
        else if (text == "🗓️ Расписание")
            text = "/schedule";
        else if (text == "📅 Дата расчета")
            text = "/calcfrom";
        else if (text == "🔄 Пересчитать")
            text = "/recalc";
        else if (text == "🏷️ Флаг НДФЛ")
            text = "/ndflflag";
        else if (text == "📌 Дата НДФЛ")
            text = "/ndflfrom";
        else if (text == "✏️ Изменить сумму")
            text = "/editamount";
        else if (text == "💸 Выплата")
        {
            _state.SetState(chatId, nameof(BotAwaitState.PayAmountInput));

            await bot.SendMessage(
                chatId,
                """
                💸 Учет полученной выплаты

                Шаг 1: введите сумму, которую вы фактически получили.
                Примеры:
                100000
                75500
                """,
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return;
        }
        else if (text == "➕ Начисление")
        {
            _state.SetState(chatId, nameof(BotAwaitState.ScheduleAddInput));

            await bot.SendMessage(
                chatId,
"""
➕ Добавление начисления

Введите строку в формате:
<день_месяца> <сумма_на_руки>

Примеры:
23 150000
15 75000

Где:
день месяца: 1-31
сумма: только число
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return;
        }
        else if (text == "➖ Начисление")
        {
            _state.SetState(chatId, nameof(BotAwaitState.ScheduleDelInput));

            await bot.SendMessage(
                chatId,
"""
➖ Удаление начисления

Введите день месяца для удаления из расписания.
Примеры:
23
15
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return;
        }
        else if (text == "⏰ Время начисления")
        {
            _state.SetState(chatId, nameof(BotAwaitState.ScheduleTimeInput));

            await bot.SendMessage(
                chatId,
"""
⏰ Время автопроверки

Введите время в формате ЧЧ:ММ (24 часа).
Примеры:
00:05
12:00
23:30
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return;
        }

        if (text == "/calcfrom")
        {
            _state.SetState(chatId, nameof(BotAwaitState.CalculationMonthInput));

            await bot.SendMessage(
                chatId,
"""
📅 Дата начала расчета долга

Введите дату, с которой считать баланс.
Можно так:
- ДД.ММ.ГГГГ (например: 15.11.2025)
- ММ.ГГГГ (например: 11.2025, день будет 1)
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return;
        }

        if (text.StartsWith("/calcfrom ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var arg = text["/calcfrom ".Length..].Trim();
                var (day, month, year) = ParseCalculationDate(arg);
                var result = await scheduleService.SetCalculationStartDateAsync(chatId, day, month, year);
                await bot.SendMessage(chatId, result, replyMarkup: GetMainKeyboard(), cancellationToken: token);
            }
            catch
            {
                await bot.SendMessage(chatId, "Формат: /calcfrom 15.11.2025 или /calcfrom 11.2025", cancellationToken: token);
            }
            return;
        }

        if (text == "/status")
        {
            var result = await salaryService.GetStatus(chatId);

            await bot.SendMessage(chatId, result, cancellationToken: token);
            return;
        }

        if (text == "/users")
        {
            var adminIds = _config.GetSection("Telegram:AdminUserIds").Get<long[]>() ?? [];
            if (!adminIds.Contains(chatId))
            {
                await bot.SendMessage(chatId, "Нет доступа.", cancellationToken: token);
                return;
            }

            var count = await salaryService.GetUserCount();
            await bot.SendMessage(chatId, $"Пользователей: {count}", cancellationToken: token);
            return;
        }

        if (text == "/recalc")
        {
            var result = await scheduleService.RecalculateAccrualsAsync(chatId, salaryService);
            await bot.SendMessage(chatId, result, cancellationToken: token);
            return;
        }

        if (text == "/ndflflag")
        {
            var result = await scheduleService.ToggleNdflAsync(chatId);
            await bot.SendMessage(chatId, result, cancellationToken: token);
            return;
        }

        if (text == "/ndflfrom")
        {
            _state.SetState(chatId, nameof(BotAwaitState.NdflFromInput));
            await bot.SendMessage(
                chatId,
"""
📌 Дата начала применения НДФЛ

Введите дату, начиная с которой НДФЛ учитывается в расчетах.
Можно так:
- ДД.ММ.ГГГГ (например: 01.01.2026)
- ММ.ГГГГ (например: 01.2026, день будет 1)
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return;
        }

        if (text.StartsWith("/ndflfrom ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var arg = text["/ndflfrom ".Length..].Trim();
                var (day, month, year) = ParseCalculationDate(arg);
                var result = await scheduleService.SetNdflStartDateAsync(chatId, day, month, year);
                await bot.SendMessage(chatId, result, replyMarkup: GetMainKeyboard(), cancellationToken: token);
            }
            catch
            {
                await bot.SendMessage(chatId, "Формат: /ndflfrom 01.01.2026 или /ndflfrom 01.2026", cancellationToken: token);
            }
            return;
        }

        if (text == "/history")
        {
            var result = await salaryService.GetHistory(chatId);

            await bot.SendMessage(
                chatId,
                $"<pre>{WebUtility.HtmlEncode(result)}</pre>",
                parseMode: ParseMode.Html,
                cancellationToken: token);
            return;
        }

        if (text == "/editamount" || text == "/editpay")
        {
            _state.SetState(chatId, nameof(BotAwaitState.EditPayInput));
            await bot.SendMessage(
                chatId,
"""
✏️ Точечное изменение суммы

Введите:
<дата> <новая_сумма>
или
<дата> <новая_сумма> получил

Примеры:
30.11.2025 85000
30.11.2025 85000 получил

Без "получил" меняется начисление.
С "получил" меняется выплата.
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return;
        }

        if (text.StartsWith("/editamount ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleEditPayInput(
                bot,
                salaryService,
                chatId,
                text["/editamount ".Length..],
                token);
            return;
        }

        if (text.StartsWith("/editpay ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleEditPayInput(
                bot,
                salaryService,
                chatId,
                text["/editpay ".Length..],
                token);
            return;
        }

        if (text.StartsWith("/schedule"))
        {
            await HandleScheduleCommand(bot, chatId, scheduleService, text, token);
            return;
        }

        if (text.StartsWith("/pay"))
        {
            try
            {
                var split = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                decimal amount = decimal.Parse(split[1], CultureInfo.InvariantCulture);

                DateTime date =
                    split.Length >= 3
                    ? ParseUserDate(split[2])
                    : DateTime.Now;

                await salaryService.AddPayment(chatId, amount, date);

                await bot.SendMessage(
                    chatId,
                    "Полученная выплата сохранена",
                    cancellationToken: token);
            }
            catch
            {
                await bot.SendMessage(
                    chatId,
                    "Ошибка команды",
                    cancellationToken: token);
            }

            return;
        }
    }

    private async Task HandleCallbackQuery(
        ITelegramBotClient bot,
        CallbackQuery callbackQuery,
        SalaryService salaryService,
        SalaryScheduleService scheduleService,
        CancellationToken token)
    {
        var data = callbackQuery.Data;
        if (string.IsNullOrWhiteSpace(data))
            return;

        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        await scheduleService.EnsureSeededForChatAsync(chatId);
        switch (data)
        {
            case CbMain:
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    "Выберите действие:",
                    await GetMainKeyboardAsync(scheduleService, chatId),
                    token);
                break;
            case CbStatus:
            {
                var result = await salaryService.GetStatus(chatId);
                await EditCallbackMessage(bot, callbackQuery, result, await GetMainKeyboardAsync(scheduleService, chatId), token);
                break;
            }
            case CbHistory:
            {
                var result = await salaryService.GetHistory(chatId);
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    $"<pre>{WebUtility.HtmlEncode(result)}</pre>",
                    await GetMainKeyboardAsync(scheduleService, chatId),
                    token,
                    ParseMode.Html);
                break;
            }
            case CbPay:
                _state.SetState(chatId, nameof(BotAwaitState.PayAmountInput));
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    """
💸 Учет полученной выплаты

Шаг 1: введите сумму, которую вы фактически получили (только число, без пробелов и \"₽\").
Примеры:
100000
75500

После этого бот попросит дату.
""",
                    GetBackKeyboard(CbMain),
                    token);
                break;
            case CbSettings:
            {
                var settingsText = await BuildSettingsSummaryAsync(scheduleService, chatId);
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    settingsText,
                    await GetSettingsKeyboardAsync(scheduleService, chatId),
                    token);
                break;
            }
            case CbSchedule:
            {
                var result = await scheduleService.FormatScheduleAsync(chatId);
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    result,
                    await GetSettingsKeyboardAsync(scheduleService, chatId),
                    token);
                break;
            }
            case CbScheduleAdd:
            _state.SetState(chatId, nameof(BotAwaitState.ScheduleAddInput));
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    """
➕ Добавление начисления

Введите строку в формате:
<день_месяца> <сумма_на_руки>

Примеры:
23 150000
15 75000

Где:
- день месяца: от 1 до 31
- сумма: только число
""",
                    GetBackKeyboard(CbSettings),
                    token);
                break;
            case CbScheduleDel:
            _state.SetState(chatId, nameof(BotAwaitState.ScheduleDelInput));
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    """
➖ Удаление начисления

Введите день месяца, который нужно удалить из расписания.
Примеры:
23
15
""",
                    GetBackKeyboard(CbSettings),
                    token);
                break;
            case CbScheduleTime:
            _state.SetState(chatId, nameof(BotAwaitState.ScheduleTimeInput));
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    """
⏰ Время автопроверки

Введите время в формате ЧЧ:ММ (24 часа).
Примеры:
00:05
12:00
23:30
""",
                    GetBackKeyboard(CbSettings),
                    token);
                break;
            case CbCalcFrom:
            _state.SetState(chatId, nameof(BotAwaitState.CalculationMonthInput));
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    """
📅 Дата начала расчета долга

Введите дату, с которой считать баланс.
Можно так:
- ДД.ММ.ГГГГ (например: 15.11.2025)
- ММ.ГГГГ (например: 11.2025, день будет 1)
""",
                    GetBackKeyboard(CbSettings),
                    token);
                break;
            case CbRecalc:
            {
                var result = await scheduleService.RecalculateAccrualsAsync(chatId, salaryService);
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    result,
                    await GetSettingsKeyboardAsync(scheduleService, chatId),
                    token);
                break;
            }
            case CbNdflFlag:
            {
                var result = await scheduleService.ToggleNdflAsync(chatId);
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    result,
                    await GetSettingsKeyboardAsync(scheduleService, chatId),
                    token);
                break;
            }
            case CbNdflFrom:
            _state.SetState(chatId, nameof(BotAwaitState.NdflFromInput));
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    """
📌 Дата начала применения НДФЛ

Введите дату, начиная с которой НДФЛ учитывается в расчетах.
Можно так:
- ДД.ММ.ГГГГ (например: 01.01.2026)
- ММ.ГГГГ (например: 01.2026, день будет 1)
""",
                    GetBackKeyboard(CbSettings),
                    token);
                break;
            case CbEditAmount:
            _state.SetState(chatId, nameof(BotAwaitState.EditPayInput));
                await EditCallbackMessage(
                    bot,
                    callbackQuery,
                    """
✏️ Точечное изменение суммы

Введите:
<дата> <новая_сумма>
или
<дата> <новая_сумма> получил

Примеры:
30.11.2025 85000
30.11.2025 85000 получил

Без слова "получил" меняется начисление.
Со словом "получил" меняется выплата.
""",
                    GetBackKeyboard(CbSettings),
                    token);
                break;
            default:
                break;
        }

        await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: token);
    }

    private static async Task EditCallbackMessage(
        ITelegramBotClient bot,
        CallbackQuery callbackQuery,
        string text,
        InlineKeyboardMarkup keyboard,
        CancellationToken token,
        ParseMode? parseMode = null)
    {
        if (callbackQuery.Message is not { } msg)
            return;

        await SafeEditMessageText(
            bot,
            msg.Chat.Id,
            msg.MessageId,
            text,
            keyboard,
            token,
            parseMode ?? ParseMode.None);
    }

    private static async Task SafeEditMessageText(
        ITelegramBotClient bot,
        long chatId,
        int messageId,
        string text,
        InlineKeyboardMarkup keyboard,
        CancellationToken token,
        ParseMode parseMode = ParseMode.None)
    {
        try
        {
            await bot.EditMessageText(
                chatId,
                messageId,
                text,
                replyMarkup: keyboard,
                parseMode: parseMode,
                cancellationToken: token);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Ignore repeated click when text/markup are unchanged.
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
        ITelegramBotClient bot,
        long chatId,
        SalaryScheduleService scheduleService,
        string text,
        CancellationToken token)
    {
        if (text == "/schedule")
        {
            var result = await scheduleService.FormatScheduleAsync(chatId);
            await bot.SendMessage(chatId, result, cancellationToken: token);
            return;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            await bot.SendMessage(chatId, "Неизвестная подкоманда.", cancellationToken: token);
            return;
        }

        var sub = parts[1].ToLowerInvariant();
        string resultMessage;

        try
        {
            resultMessage = sub switch
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
        }
        catch
        {
            resultMessage = "Ошибка команды. Проверьте формат.";
        }

        await bot.SendMessage(chatId, resultMessage, cancellationToken: token);
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

    private Task HandleError(
        ITelegramBotClient bot,
        Exception ex,
        CancellationToken token)
    {
        _logger.LogError(ex, "Telegram bot error");

        return Task.CompletedTask;
    }

    private async Task<bool> HandlePayAmountInput(
        ITelegramBotClient bot,
        long chatId,
        string text,
        CancellationToken token)
    {
        try
        {
            var amount = decimal.Parse(text, CultureInfo.InvariantCulture);
            _state.SetPendingAmount(chatId, amount);
            _state.SetState(chatId, nameof(BotAwaitState.PayDateInput));

            await bot.SendMessage(
                chatId,
"""
Шаг 2: введите дату выплаты.

Форматы:
- ДД.ММ.ГГГГ (например: 30.11.2025)
- YYYY-MM-DD (например: 2025-11-30)
- или напишите: сегодня
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);

            return false;
        }
        catch
        {
            await bot.SendMessage(
                chatId,
"""
Не смог распознать сумму.
Введите только число.

Примеры:
100000
75500
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return false;
        }
    }

    private async Task<bool> HandlePayDateInput(
        ITelegramBotClient bot,
        SalaryService salaryService,
        long chatId,
        string text,
        CancellationToken token)
    {
        var amount = _state.GetPendingAmount(chatId);
        if (amount is null)
        {
            _state.RemoveState(chatId);
            await bot.SendMessage(chatId, "Сумма не найдена. Нажмите 'Выплата' снова.", replyMarkup: GetMainKeyboard(), cancellationToken: token);
            return true;
        }

        try
        {
            DateTime date = text.Equals("сегодня", StringComparison.OrdinalIgnoreCase)
                ? DateTime.Now
                : ParseUserDate(text);

            await salaryService.AddPayment(chatId, amount.Value, date);
            _state.RemovePendingAmount(chatId);

            await bot.SendMessage(
                chatId,
                "Полученная выплата сохранена",
                replyMarkup: GetMainKeyboard(),
                cancellationToken: token);

            return true;
        }
        catch
        {
            await bot.SendMessage(
                chatId,
"""
Не смог распознать дату.

Примеры:
30.11.2025
2025-11-30
сегодня
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return false;
        }
    }

    private static async Task<bool> HandleScheduleAddInput(
        ITelegramBotClient bot,
        SalaryScheduleService scheduleService,
        long chatId,
        string text,
        CancellationToken token)
    {
        try
        {
            var split = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var day = int.Parse(split[0], CultureInfo.InvariantCulture);
            var amount = decimal.Parse(split[1], CultureInfo.InvariantCulture);

            var message = await scheduleService.AddOrUpdateRuleAsync(chatId, day, amount);
            await bot.SendMessage(chatId, message, replyMarkup: GetMainKeyboard(), cancellationToken: token);
            return true;
        }
        catch
        {
            await bot.SendMessage(
                chatId,
"""
Не смог распознать ввод.

Нужен формат:
<день_месяца> <сумма>

Пример:
23 150000
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return false;
        }
    }

    private static async Task<bool> HandleScheduleDeleteInput(
        ITelegramBotClient bot,
        SalaryScheduleService scheduleService,
        long chatId,
        string text,
        CancellationToken token)
    {
        try
        {
            var day = int.Parse(text, CultureInfo.InvariantCulture);
            var message = await scheduleService.RemoveRuleAsync(chatId, day);
            await bot.SendMessage(chatId, message, replyMarkup: GetMainKeyboard(), cancellationToken: token);
            return true;
        }
        catch
        {
            await bot.SendMessage(
                chatId,
"""
Не смог распознать ввод.

Введите только день месяца (число от 1 до 31).

Пример:
23
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return false;
        }
    }

    private static async Task<bool> HandleScheduleTimeInput(
        ITelegramBotClient bot,
        SalaryScheduleService scheduleService,
        long chatId,
        string text,
        CancellationToken token)
    {
        try
        {
            var message = await SetTimeFromCommand(scheduleService, chatId, text);
            await bot.SendMessage(chatId, message, replyMarkup: GetMainKeyboard(), cancellationToken: token);
            return true;
        }
        catch
        {
            await bot.SendMessage(
                chatId,
"""
Не смог распознать время.

Формат: ЧЧ:ММ (24 часа)
Примеры:
12:00
00:05
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return false;
        }
    }

    private static async Task<bool> HandleCalculationMonthInput(
        ITelegramBotClient bot,
        SalaryScheduleService scheduleService,
        long chatId,
        string text,
        CancellationToken token)
    {
        try
        {
            var (day, month, year) = ParseCalculationDate(text);
            var result = await scheduleService.SetCalculationStartDateAsync(chatId, day, month, year);
            await bot.SendMessage(chatId, result, replyMarkup: GetMainKeyboard(), cancellationToken: token);
            return true;
        }
        catch
        {
            await bot.SendMessage(
                chatId,
"""
Не смог распознать дату.

Можно так:
15.11.2025
11.2025
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return false;
        }
    }

    private static async Task<bool> HandleNdflFromInput(
        ITelegramBotClient bot,
        SalaryScheduleService scheduleService,
        long chatId,
        string text,
        CancellationToken token)
    {
        try
        {
            var (day, month, year) = ParseCalculationDate(text);
            var result = await scheduleService.SetNdflStartDateAsync(chatId, day, month, year);
            await bot.SendMessage(chatId, result, replyMarkup: GetMainKeyboard(), cancellationToken: token);
            return true;
        }
        catch
        {
            await bot.SendMessage(
                chatId,
"""
Не смог распознать дату старта НДФЛ.

Можно так:
01.01.2026
01.2026
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return false;
        }
    }

    private static async Task<bool> HandleEditPayInput(
        ITelegramBotClient bot,
        SalaryService salaryService,
        long chatId,
        string text,
        CancellationToken token)
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

            await bot.SendMessage(
                chatId,
                result,
                replyMarkup: GetMainKeyboard(),
                cancellationToken: token);
            return true;
        }
        catch
        {
            await bot.SendMessage(
                chatId,
"""
Не смог распознать ввод.

Формат:
30.11.2025 85000
30.11.2025 85000 получил

Без "получил" меняется начисление.
С "получил" меняется выплата.
""",
                replyMarkup: GetCancelKeyboard(),
                cancellationToken: token);
            return false;
        }
    }

    private static (int Day, int Month, int Year) ParseCalculationDate(string text)
    {
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            2 => (
                1,
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
        if (DateTime.TryParseExact(
                text,
                AcceptedDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed;
        }

        throw new FormatException("Invalid date format");
    }

    private static InlineKeyboardMarkup GetMainKeyboard()
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("💰 Общий долг", CbStatus), InlineKeyboardButton.WithCallbackData("📚 История долга", CbHistory)],
            [InlineKeyboardButton.WithCallbackData("💸 Учет полученной выплаты", CbPay)],
            [InlineKeyboardButton.WithCallbackData("⚙️ Настройки", CbSettings)]
        ]);
    }

    private Task<InlineKeyboardMarkup> GetMainKeyboardAsync(SalaryScheduleService scheduleService, long chatId)
    {
        _ = scheduleService;
        _ = chatId;
        return Task.FromResult(GetMainKeyboard());
    }

    private async Task<InlineKeyboardMarkup> GetSettingsKeyboardAsync(SalaryScheduleService scheduleService, long chatId)
    {
        var ndflEnabled = await scheduleService.IsNdflEnabledAsync(chatId);
        var ndflLabel = $"🏷️ НДФЛ: {(ndflEnabled ? "вкл" : "выкл")}";

        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("📆 Правила начислений", CbSchedule)],
            [InlineKeyboardButton.WithCallbackData("➕ Добавить правило", CbScheduleAdd), InlineKeyboardButton.WithCallbackData("➖ Удалить правило", CbScheduleDel)],
            [InlineKeyboardButton.WithCallbackData("🕒 Время проверки", CbScheduleTime), InlineKeyboardButton.WithCallbackData("📅 Дата начала расчета", CbCalcFrom)],
            [InlineKeyboardButton.WithCallbackData(ndflLabel, CbNdflFlag), InlineKeyboardButton.WithCallbackData("📌 Дата старта НДФЛ", CbNdflFrom)],
            [InlineKeyboardButton.WithCallbackData("✏️ Изменить сумму записи", CbEditAmount), InlineKeyboardButton.WithCallbackData("🔄 Пересчитать начисления", CbRecalc)],
            [InlineKeyboardButton.WithCallbackData("⬅️ Назад", CbMain)]
        ]);
    }

    private static InlineKeyboardMarkup GetBackKeyboard(string backCallbackData)
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("⬅️ Назад", backCallbackData)]
        ]);
    }

    private static ReplyKeyboardMarkup GetCancelKeyboard()
    {
        return new ReplyKeyboardMarkup(
        [
            [new KeyboardButton("Отмена")]
        ])
        {
            ResizeKeyboard = true
        };
    }

    private enum BotAwaitState
    {
        PayAmountInput,
        PayDateInput,
        ScheduleAddInput,
        ScheduleDelInput,
        ScheduleTimeInput,
        CalculationMonthInput,
        NdflFromInput,
        EditPayInput
    }
}
