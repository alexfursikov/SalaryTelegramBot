using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using SalaryTelegramBot.Api.Services;
using Telegram.Bot.Types;

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

            _logger.LogInformation("Webhook received raw JSON ({Len} bytes)", raw.Length);

            var update = JsonConvert.DeserializeObject<Update>(raw);
            if (update is null)
            {
                _logger.LogWarning("Failed to deserialize update");
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
