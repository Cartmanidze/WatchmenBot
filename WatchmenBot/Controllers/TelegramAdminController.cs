using Microsoft.AspNetCore.Mvc;
using WatchmenBot.Features.Admin;
using WatchmenBot.Services;

namespace WatchmenBot.Controllers;

[ApiController]
[Route("admin")]
public class TelegramAdminController : ControllerBase
{
    private readonly SetWebhookHandler _setWebhookHandler;
    private readonly DeleteWebhookHandler _deleteWebhookHandler;
    private readonly GetWebhookInfoHandler _getWebhookInfoHandler;
    private readonly ChatImportService _chatImportService;

    public TelegramAdminController(
        SetWebhookHandler setWebhookHandler,
        DeleteWebhookHandler deleteWebhookHandler,
        GetWebhookInfoHandler getWebhookInfoHandler,
        ChatImportService chatImportService)
    {
        _setWebhookHandler = setWebhookHandler;
        _deleteWebhookHandler = deleteWebhookHandler;
        _getWebhookInfoHandler = getWebhookInfoHandler;
        _chatImportService = chatImportService;
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

    /// <summary>
    /// Import chat history from Telegram Desktop export directory
    /// </summary>
    /// <param name="request">Import parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result</returns>
    [HttpPost("import-chat")]
    public async Task<IActionResult> ImportChat([FromBody] ImportChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ExportPath))
        {
            return BadRequest("ExportPath is required");
        }

        if (request.ChatId == 0)
        {
            return BadRequest("ChatId is required");
        }

        var result = await _chatImportService.ImportFromDirectoryAsync(
            request.ExportPath,
            request.ChatId,
            request.SkipExisting,
            cancellationToken);

        if (!result.IsSuccess)
        {
            return Problem(result.ErrorMessage);
        }

        return Ok(result);
    }
}

public class ImportChatRequest
{
    /// <summary>
    /// Path to Telegram Desktop export directory on server
    /// </summary>
    public string ExportPath { get; set; } = string.Empty;

    /// <summary>
    /// Target chat ID to import messages into
    /// </summary>
    public long ChatId { get; set; }

    /// <summary>
    /// Skip messages that already exist in database (default: true)
    /// </summary>
    public bool SkipExisting { get; set; } = true;
}