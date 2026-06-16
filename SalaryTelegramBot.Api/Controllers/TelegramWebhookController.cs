using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SalaryTelegramBot.Api.Services;
using Telegram.Bot.Types;

namespace SalaryTelegramBot.Api.Controllers;

[Route("webhook")]
public class TelegramWebhookController : ControllerBase
{
    private static readonly JsonSerializer Serializer = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Converters = { new Newtonsoft.Json.Converters.UnixDateTimeConverter() }
    };

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

            _logger.LogInformation("Webhook received {Len} bytes, keys: {Keys}",
                raw.Length, string.Join(",", json.Properties().Select(p => p.Name)));

            Update? update = null;

            if (json["message"] is not null)
                update = json["message"]!.ToObject<Message>(Serializer) is { } msg
                    ? new Update { Id = json["update_id"]!.Value<int>(), Message = msg }
                    : null;
            else if (json["callback_query"] is not null)
                update = json["callback_query"]!.ToObject<CallbackQuery>(Serializer) is { } cq
                    ? new Update { Id = json["update_id"]!.Value<int>(), CallbackQuery = cq }
                    : null;

            if (update is null)
            {
                _logger.LogWarning("Could not parse update");
                return Ok();
            }

            _logger.LogInformation("Parsed update {Id}, type={Type}", update.Id, update.Type);
            await _handler.HandleUpdateAsync(update, ct);
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
