using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using SalaryTelegramBot.Api.Services;

namespace SalaryTelegramBot.Api.Controllers;

[Route("webhook")]
public class TelegramWebhookController : ControllerBase
{
    private readonly TelegramBotService _handler;
    private readonly ILogger<TelegramWebhookController> _logger;

    public TelegramWebhookController(TelegramBotService handler, ILogger<TelegramWebhookController> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken ct)
    {
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
                _logger.LogInformation("Message update {Id}: chat={Chat}, text={Text}", updateId, chatId, text);
                await _handler.HandleMessageAsync(chatId, text, ct);
            }
            else if (json["callback_query"] is JObject cq)
            {
                var chatId = cq.SelectToken("message.chat.id")?.Value<long>()
                    ?? cq.SelectToken("from.id")?.Value<long>() ?? 0;
                var data = cq["data"]?.Value<string>() ?? "";
                var cqId = cq["id"]?.Value<string>() ?? "";
                _logger.LogInformation("Callback update {Id}: chat={Chat}, data={Data}", updateId, chatId, data);
                await _handler.HandleCallbackAsync(chatId, data, cqId, ct);
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
