#r "nuget: HtmlAgilityPack, 1.11.61"
using HtmlAgilityPack;
using System.Text.RegularExpressions;

var html = File.ReadAllText(@"C:\Users\PierDunn\Downloads\Telegram Desktop\ChatExport_2025-12-18\messages.html");
var doc = new HtmlDocument();
doc.LoadHtml(html);

var messageNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'message') and contains(@class, 'default')]");
Console.WriteLine($"Found {messageNodes?.Count ?? 0} message nodes");

if (messageNodes != null)
{
    foreach (var node in messageNodes.Take(3))
    {
        var id = node.GetAttributeValue("id", "");
        var fromName = node.SelectSingleNode(".//div[@class='from_name']")?.InnerText?.Trim();
        var text = node.SelectSingleNode(".//div[@class='text']")?.InnerText?.Trim();
        var dateNode = node.SelectSingleNode(".//div[contains(@class, 'date') and contains(@class, 'details')]");
        var dateTitle = dateNode?.GetAttributeValue("title", "");
        Console.WriteLine($"ID: {id}, From: {fromName ?? "(none)"}, Date: {dateTitle}, Text: {text?.Substring(0, Math.Min(30, text?.Length ?? 0))}...");
    }
}
