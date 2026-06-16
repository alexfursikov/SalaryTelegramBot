using Microsoft.AspNetCore.Mvc;
using SalaryTelegramBot.Api.Services;
using Telegram.Bot.Types;

namespace SalaryTelegramBot.Api.Controllers;

[ApiController]
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
    public async Task<IActionResult> Post([FromBody] Update update, CancellationToken ct)
    {
        _logger.LogInformation("Webhook received update {Id}", update.Id);
        await _handler.HandleUpdateAsync(update, ct);
        return Ok();
    }

    [HttpGet]
    public IActionResult Health() => Ok("ok");
}
