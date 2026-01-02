using Microsoft.AspNetCore.Mvc;
using Telegram.Bot.Types;
using WatchmenBot.Features.Webhook;

namespace WatchmenBot.Controllers;

[ApiController]
[Route("telegram")]
public class TelegramWebhookController(ProcessTelegramUpdateHandler handler) : ControllerBase
{
    [HttpPost("update")]
    public async Task<IActionResult> Post([FromBody] Update update, CancellationToken cancellationToken)
    {
        var request = new ProcessTelegramUpdateRequest
        {
            Update = update,
            RemoteIpAddress = HttpContext.Connection.RemoteIpAddress,
            Headers = Request.Headers
        };

        var response = await handler.HandleAsync(request, cancellationToken);

        if (!response.IsSuccess)
        {
            return StatusCode(response.StatusCode, response.ErrorMessage);
        }

        return Ok();
    }
}