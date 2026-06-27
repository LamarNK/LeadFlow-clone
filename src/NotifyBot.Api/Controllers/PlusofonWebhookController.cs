using Microsoft.AspNetCore.Mvc;
using NotifyBot.Api.Dto;
using NotifyBot.Application.Abstractions;

namespace NotifyBot.Api.Controllers;

[ApiController]
[Route("api/webhooks")]
public sealed class PlusofonWebhookController(
    ISmsRoutingService smsRoutingService,
    ILogger<PlusofonWebhookController> logger) : ControllerBase
{
    [HttpPost("plusofon")]
    public async Task<IActionResult> ReceivePlusofonWebhook(
        [FromBody] PlusofonWebhookDto? payload,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Plusofon webhook received");

            var messageText = payload?.GetMessageText();
            await smsRoutingService.ProcessWebhookAsync(messageText ?? string.Empty, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error while processing Plusofon webhook");
        }

        return Ok();
    }
}