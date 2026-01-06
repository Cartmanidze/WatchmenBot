using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace WatchmenBot.Features.Messages.Services;

/// <summary>
/// Parses Telegram Desktop HTML export files
/// </summary>
public class TelegramExportParser(ILogger<TelegramExportParser> logger)
{
    // Regex to extract message ID from id attribute (e.g., "message41123")
    private static readonly Regex MessageIdRegex = new(@"message(\d+)", RegexOptions.Compiled);

    // Regex to parse datetime from title (e.g., "24.04.2025 13:49:04 UTC+06:00")
    private static readonly Regex DateTimeRegex = new(
        @"(\d{2})\.(\d{2})\.(\d{4})\s+(\d{2}):(\d{2}):(\d{2})\s+UTC([+-]\d{2}:\d{2})",
        RegexOptions.Compiled);

    /// <summary>
    /// Get chat title from export
    /// </summary>
    public string? GetChatTitleFromExport(string directoryPath)
    {
        var messagesFile = Path.Combine(directoryPath, "messages.html");
        if (!File.Exists(messagesFile))
            return null;

        try
        {
            var html = File.ReadAllText(messagesFile);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Chat title is in: <div class="text bold">Chat Title</div>
            var titleNode = doc.DocumentNode.SelectSingleNode("//div[@class='page_header']//div[@class='text bold']");
            return titleNode != null ? HtmlEntity.DeEntitize(titleNode.InnerText.Trim()) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[TelegramExport] Failed to extract chat title from {Path}", directoryPath);
            return null;
        }
    }

    /// <summary>
    /// Parse all HTML files in export directory
    /// </summary>
    public async Task<List<ImportedMessage>> ParseExportDirectoryAsync(
        string directoryPath,
        long targetChatId,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Export directory not found: {directoryPath}");

        var htmlFiles = Directory.GetFiles(directoryPath, "messages*.html")
            .OrderBy(GetFileOrder)
            .ToList();

        if (htmlFiles.Count == 0)
            throw new FileNotFoundException("No messages*.html files found in export directory");

        logger.LogInformation("[Import] Found {Count} HTML files to parse", htmlFiles.Count);

        var allMessages = new List<ImportedMessage>();
        string? lastFromName = null;

        foreach (var file in htmlFiles)
        {
            ct.ThrowIfCancellationRequested();

            var messages = await ParseHtmlFileAsync(file, targetChatId, lastFromName, ct);

            if (messages.Count > 0)
            {
                lastFromName = messages.Last().DisplayName;
                allMessages.AddRange(messages);
            }

            logger.LogInformation("[Import] Parsed {File}: {Count} messages",
                Path.GetFileName(file), messages.Count);
        }

        logger.LogInformation("[Import] Total parsed: {Count} messages", allMessages.Count);
        return allMessages;
    }

    private async Task<List<ImportedMessage>> ParseHtmlFileAsync(
        string filePath,
        long targetChatId,
        string? lastFromName,
        CancellationToken ct)
    {
        var html = await File.ReadAllTextAsync(filePath, ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var messages = new List<ImportedMessage>();
        var currentFromName = lastFromName;

        // Select messages with class containing "message default" (regular messages, not service messages)
        var messageNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'message default')]");

        if (messageNodes == null)
        {
            logger.LogWarning("[Import] No message nodes found in {File}. Trying alternative selector...", Path.GetFileName(filePath));

            // Fallback: try finding any div with id starting with "message"
            messageNodes = doc.DocumentNode.SelectNodes("//div[starts-with(@id, 'message') and not(contains(@class, 'service'))]");

            if (messageNodes == null)
            {
                logger.LogWarning("[Import] Alternative selector also found no messages in {File}", Path.GetFileName(filePath));
                return messages;
            }
        }

        logger.LogInformation("[Import] Found {Count} message nodes in {File}", messageNodes.Count, Path.GetFileName(filePath));

        foreach (var node in messageNodes)
        {
            try
            {
                var message = ParseMessageNode(node, targetChatId, ref currentFromName);
                if (message != null)
                {
                    messages.Add(message);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Import] Failed to parse message node");
            }
        }

        return messages;
    }

    private ImportedMessage? ParseMessageNode(HtmlNode node, long targetChatId, ref string? currentFromName)
    {
        // Get message ID
        var idAttr = node.GetAttributeValue("id", "");
        var idMatch = MessageIdRegex.Match(idAttr);
        if (!idMatch.Success)
        {
            logger.LogWarning("[Import] No message ID found in node: {Id}", idAttr);
            return null;
        }

        var messageId = long.Parse(idMatch.Groups[1].Value);

        // Get from_name (if present - joined messages don't have it)
        var fromNameNode = node.SelectSingleNode(".//div[@class='from_name']");
        if (fromNameNode != null)
        {
            currentFromName = HtmlEntity.DeEntitize(fromNameNode.InnerText.Trim());
        }

        if (string.IsNullOrWhiteSpace(currentFromName))
        {
            logger.LogWarning("[Import] No from_name for message {Id}", messageId);
            return null;
        }

        // Get date/time - try multiple selectors
        var dateNode = node.SelectSingleNode(".//div[contains(@class, 'date details')]")
                    ?? node.SelectSingleNode(".//div[contains(@class, 'date') and contains(@class, 'details')]")
                    ?? node.SelectSingleNode(".//div[@class='pull_right date details']");
        var dateTitle = dateNode?.GetAttributeValue("title", "");
        var dateUtc = ParseDateTime(dateTitle);

        if (dateUtc == null)
        {
            logger.LogWarning("[Import] Failed to parse date for message {Id}: '{DateTitle}'", messageId, dateTitle);
            return null;
        }

        // Get text - try multiple selectors
        var textNode = node.SelectSingleNode(".//div[@class='text']")
                    ?? node.SelectSingleNode(".//div[contains(@class, 'text')]");
        var text = textNode != null ? CleanMessageText(textNode.InnerHtml) : null;

        // Skip empty messages
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Get reply_to if present
        long? replyToId = null;
        var replyNode = node.SelectSingleNode(".//div[contains(@class, 'reply_to')]");
        if (replyNode != null)
        {
            var replyLink = replyNode.SelectSingleNode(".//a[@href]");
            var href = replyLink?.GetAttributeValue("href", "");
            if (!string.IsNullOrEmpty(href) && href.StartsWith("message"))
            {
                var replyMatch = MessageIdRegex.Match(href);
                if (replyMatch.Success)
                {
                    replyToId = long.Parse(replyMatch.Groups[1].Value);
                }
            }
        }

        return new ImportedMessage
        {
            Id = messageId,
            ChatId = targetChatId,
            FromUserId = 0, // Unknown from HTML export
            Username = null,
            DisplayName = currentFromName,
            Text = text,
            DateUtc = dateUtc.Value,
            ReplyToMessageId = replyToId,
            HasLinks = text.Contains("http://") || text.Contains("https://"),
            HasMedia = node.SelectSingleNode(".//div[contains(@class, 'media')]") != null
        };
    }

    private DateTimeOffset? ParseDateTime(string? dateTitle)
    {
        if (string.IsNullOrWhiteSpace(dateTitle))
            return null;

        var match = DateTimeRegex.Match(dateTitle);
        if (!match.Success)
            return null;

        try
        {
            var day = int.Parse(match.Groups[1].Value);
            var month = int.Parse(match.Groups[2].Value);
            var year = int.Parse(match.Groups[3].Value);
            var hour = int.Parse(match.Groups[4].Value);
            var minute = int.Parse(match.Groups[5].Value);
            var second = int.Parse(match.Groups[6].Value);
            // TimeSpan.Parse doesn't handle leading '+', so we need to remove it
            var offsetStr = match.Groups[7].Value.TrimStart('+');
            var offset = TimeSpan.Parse(offsetStr);

            var dt = new DateTime(year, month, day, hour, minute, second);
            var dto = new DateTimeOffset(dt, offset);

            return dto.ToUniversalTime();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[TelegramExport] Failed to parse datetime from title");
            return null;
        }
    }

    private string CleanMessageText(string html)
    {
        // Decode HTML entities
        var text = HtmlEntity.DeEntitize(html);

        // Replace <br> with newlines
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

        // Extract href from links but keep display text
        text = Regex.Replace(text, @"<a[^>]*href=""([^""]+)""[^>]*>([^<]*)</a>", "$2 ($1)", RegexOptions.IgnoreCase);

        // Remove remaining HTML tags
        text = Regex.Replace(text, @"<[^>]+>", "");

        // Clean up whitespace
        text = text.Trim();

        return text;
    }

    private int GetFileOrder(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        // messages.html -> 1, messages2.html -> 2, etc.
        var numPart = fileName.Replace("messages", "");
        return string.IsNullOrEmpty(numPart) ? 1 : int.Parse(numPart);
    }
}

public class ImportedMessage
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public long FromUserId { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public string? Text { get; set; }
    public DateTimeOffset DateUtc { get; set; }
    public long? ReplyToMessageId { get; set; }
    public bool HasLinks { get; set; }
    public bool HasMedia { get; set; }
}
