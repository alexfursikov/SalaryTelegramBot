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
        var update = body.Deserialize<Update>();
        if (update is null)
            return Ok();

        _logger.LogInformation("Webhook received update {Id}", update.Id);
        await _handler.HandleUpdateAsync(update, ct);
        return Ok();
    }

    [HttpGet]
    public IActionResult Health() => Ok("ok");
}
