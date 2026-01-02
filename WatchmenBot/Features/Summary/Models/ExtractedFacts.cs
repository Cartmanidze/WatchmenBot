namespace WatchmenBot.Features.Summary.Models;

/// <summary>
/// Structured facts extracted from chat messages (Stage 1 of summary generation)
/// </summary>
public class ExtractedFacts
{
    public List<EventFact> Events { get; set; } = [];
    public List<DiscussionFact> Discussions { get; set; } = [];
    public List<QuoteFact> Quotes { get; set; } = [];
    public List<HeroFact> Heroes { get; set; } = [];
}

public class EventFact
{
    public string What { get; set; } = string.Empty;
    public List<string> Who { get; set; } = [];
    public string? Time { get; set; }
}

public class DiscussionFact
{
    public string Topic { get; set; } = string.Empty;
    public List<string> Participants { get; set; } = [];
    public string Summary { get; set; } = string.Empty;
}

public class QuoteFact
{
    public string Text { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string? Context { get; set; }
}

public class HeroFact
{
    public string Name { get; set; } = string.Empty;
    public string Why { get; set; } = string.Empty;
}
