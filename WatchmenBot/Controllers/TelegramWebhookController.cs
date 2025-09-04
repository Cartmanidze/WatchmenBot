using Microsoft.AspNetCore.Mvc;
using Telegram.Bot.Types;
using WatchmenBot.Features.Webhook;

namespace WatchmenBot.Controllers;

[ApiController]
[Route("telegram")]
public class TelegramWebhookController : ControllerBase
{
    private readonly ProcessTelegramUpdateHandler _handler;

    public TelegramWebhookController(ProcessTelegramUpdateHandler handler)
    {
        _handler = handler;
    }

    [HttpPost("update")]
    public async Task<IActionResult> Post([FromBody] Update update, CancellationToken cancellationToken)
    {
        var request = new ProcessTelegramUpdateRequest
        {
            Update = update,
            RemoteIpAddress = HttpContext.Connection.RemoteIpAddress,
            Headers = Request.Headers
        };

        var response = await _handler.HandleAsync(request, cancellationToken);

        if (!response.IsSuccess)
        {
            return StatusCode(response.StatusCode, response.ErrorMessage);
        }

        return Ok();
    }
}