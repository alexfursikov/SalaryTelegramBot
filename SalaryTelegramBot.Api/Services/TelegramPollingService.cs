using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace SalaryTelegramBot.Api.Services;

public class TelegramPollingService : BackgroundService
{
    private readonly TelegramBotClient _bot;
    private readonly TelegramBotService _handler;
    private readonly ILogger<TelegramPollingService> _logger;

    public TelegramPollingService(
        TelegramBotClient bot,
        TelegramBotService handler,
        ILogger<TelegramPollingService> logger)
    {
        _bot = bot;
        _handler = handler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting long polling...");

        _bot.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = new[]
                {
                    UpdateType.Message,
                    UpdateType.CallbackQuery
                }
            },
            cancellationToken: stoppingToken);

        _logger.LogInformation("Long polling started.");

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            if (update.Message is { } msg)
            {
                await _handler.HandleMessageAsync(
                    msg.Chat.Id,
                    msg.Text ?? "",
                    msg.MessageId,
                    ct);
            }
            else if (update.CallbackQuery is { } cq)
            {
                var chatId = cq.Message?.Chat.Id ?? cq.From.Id;
                await _handler.HandleCallbackAsync(
                    chatId,
                    cq.Data ?? "",
                    cq.Id,
                    cq.Message?.MessageId ?? 0,
                    ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling update {UpdateId}", update.Id);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        _logger.LogError(exception, "Polling error");
        return Task.CompletedTask;
    }
}
