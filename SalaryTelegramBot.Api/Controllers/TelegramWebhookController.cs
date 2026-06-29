using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using SalaryTelegramBot.Api.Services;

namespace SalaryTelegramBot.Api.Controllers;

[Route("webhook")]
public class TelegramWebhookController : ControllerBase
{
    private readonly TelegramBotService _handler;
    private readonly ILogger<TelegramWebhookController> _logger;
    private readonly string? _secretToken;

    public TelegramWebhookController(
        TelegramBotService handler,
        ILogger<TelegramWebhookController> logger,
        IConfiguration config)
    {
        _handler = handler;
        _logger = logger;
        _secretToken = config["Telegram:SecretToken"]
            ?? Environment.GetEnvironmentVariable("TELEGRAM__SECRET_TOKEN")
            ?? Environment.GetEnvironmentVariable("TELEGRAM_SECRET_TOKEN");
    }

    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_secretToken))
        {
            if (!Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var token)
                || !string.Equals(token.ToString(), _secretToken, StringComparison.Ordinal))
            {
                _logger.LogWarning("Invalid webhook secret token from {Ip}", HttpContext.Connection.RemoteIpAddress);
                return Unauthorized();
            }
        }

        try
        {
            using var reader = new StreamReader(Request.Body);
            var raw = await reader.ReadToEndAsync(ct);
            var json = JObject.Parse(raw);

            var updateId = json["update_id"]?.Value<int>() ?? 0;

            if (json["message"] is JObject msg)
            {
                var chatId = msg.SelectToken("chat.id")?.Value<long>() ?? 0;
                var text = msg["text"]?.Value<string>() ?? "";
                var messageId = msg["message_id"]?.Value<int>() ?? 0;
                _ = Task.Run(async () =>
                {
                    try { await _handler.HandleMessageAsync(chatId, text, messageId, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogError(ex, "Error handling message {Id}", updateId); }
                });
            }
            else if (json["callback_query"] is JObject cq)
            {
                var chatId = cq.SelectToken("message.chat.id")?.Value<long>()
                    ?? cq.SelectToken("from.id")?.Value<long>() ?? 0;
                var data = cq["data"]?.Value<string>() ?? "";
                var cqId = cq["id"]?.Value<string>() ?? "";
                var messageId = cq.SelectToken("message.message_id")?.Value<int>() ?? 0;
                _ = Task.Run(async () =>
                {
                    try { await _handler.HandleCallbackAsync(chatId, data, cqId, messageId, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogError(ex, "Error handling callback {Id}", updateId); }
                });
            }
            else
            {
                _logger.LogWarning("Unknown update type, keys: {Keys}",
                    string.Join(",", json.Properties().Select(p => p.Name)));
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing webhook update");
            return Ok();
        }
    }

    [HttpGet]
    public IActionResult Health() => Ok("ok");
}
