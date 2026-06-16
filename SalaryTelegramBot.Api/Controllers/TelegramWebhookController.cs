using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
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
    public async Task<IActionResult> Post([FromBody] JsonElement body, CancellationToken ct)
    {
        try
        {
            var update = body.Deserialize<Update>();
            if (update is null)
            {
                _logger.LogWarning("Failed to deserialize update");
                return Ok();
            }

            _logger.LogInformation("Webhook received update {Id}, type={Type}", update.Id, update.Type);
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
