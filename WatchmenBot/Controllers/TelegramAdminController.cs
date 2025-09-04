using Microsoft.AspNetCore.Mvc;
using WatchmenBot.Features.Admin;

namespace WatchmenBot.Controllers;

[ApiController]
[Route("admin")]
public class TelegramAdminController : ControllerBase
{
    private readonly SetWebhookHandler _setWebhookHandler;
    private readonly DeleteWebhookHandler _deleteWebhookHandler;
    private readonly GetWebhookInfoHandler _getWebhookInfoHandler;

    public TelegramAdminController(
        SetWebhookHandler setWebhookHandler,
        DeleteWebhookHandler deleteWebhookHandler,
        GetWebhookInfoHandler getWebhookInfoHandler)
    {
        _setWebhookHandler = setWebhookHandler;
        _deleteWebhookHandler = deleteWebhookHandler;
        _getWebhookInfoHandler = getWebhookInfoHandler;
    }

    [HttpPost("set-webhook")]
    public async Task<IActionResult> SetWebhook(CancellationToken cancellationToken)
    {
        var request = new SetWebhookRequest();
        var response = await _setWebhookHandler.HandleAsync(request, cancellationToken);

        if (!response.IsSuccess)
        {
            return BadRequest(response.ErrorMessage);
        }

        return Ok(response);
    }

    [HttpPost("delete-webhook")]
    public async Task<IActionResult> DeleteWebhook(CancellationToken cancellationToken)
    {
        var request = new DeleteWebhookRequest();
        var response = await _deleteWebhookHandler.HandleAsync(request, cancellationToken);

        if (!response.IsSuccess)
        {
            return Problem(response.ErrorMessage);
        }

        return Ok(response);
    }

    [HttpGet("webhook-info")]
    public async Task<IActionResult> GetWebhookInfo(CancellationToken cancellationToken)
    {
        var request = new GetWebhookInfoRequest();
        var response = await _getWebhookInfoHandler.HandleAsync(request, cancellationToken);

        if (!response.IsSuccess)
        {
            return Problem(response.ErrorMessage);
        }

        return Ok(response);
    }
}