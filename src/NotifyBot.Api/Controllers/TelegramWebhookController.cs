using Microsoft.AspNetCore.Mvc;
using NotifyBot.Infrastructure.Telegram;
using Telegram.Bot.Types;

namespace NotifyBot.Api.Controllers;

[ApiController]
[Route("api/webhooks")]
public sealed class TelegramWebhookController(
    ITelegramUpdateHandler updateHandler,
    ILogger<TelegramWebhookController> logger) : ControllerBase
{
    [HttpPost("telegram")]
    public async Task<IActionResult> ReceiveTelegramUpdate(
        [FromBody] Update? update,
        CancellationToken cancellationToken)
    {
        if (update is null)
        {
            return Ok();
        }

        try
        {
            await updateHandler.HandleAsync(update, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error while processing Telegram update");
        }

        return Ok();
    }
}