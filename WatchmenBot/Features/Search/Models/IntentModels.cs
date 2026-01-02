namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// Classified intent of a user question
/// </summary>
public enum QueryIntent
{
    /// <summary>Question about self ("я гондон?", "какой я?")</summary>
    PersonalSelf,

    /// <summary>Question about specific person ("что за тип Глеб?", "кто такой @vasia?")</summary>
    PersonalOther,

    /// <summary>General factual question ("что такое REST?", "как сделать X?")</summary>
    Factual,

    /// <summary>Question about event ("что произошло?", "кто выиграл?")</summary>
    Event,

    /// <summary>Time-bound question ("о чём говорили вчера?", "что было на прошлой неделе?")</summary>
    Temporal,

    /// <summary>Comparison question ("кто круче, X или Y?")</summary>
    Comparison,

    /// <summary>Question involving multiple entities ("что общего между X и Y?")</summary>
    MultiEntity
}

/// <summary>
/// Type of extracted entity
/// </summary>
public enum EntityType
{
    Person,
    Topic,
    Object
}

/// <summary>
/// Type of temporal reference
/// </summary>
public enum TemporalType
{
    Absolute,
    Relative
}

/// <summary>
/// Result of LLM-based question classification
/// </summary>
public class ClassifiedQuery
{
    public required string OriginalQuestion { get; set; }
    public QueryIntent Intent { get; set; }
    public double Confidence { get; set; }

    public List<ExtractedEntity> Entities { get; set; } = [];
    public TemporalReference? TemporalRef { get; set; }
    public List<string> MentionedPeople { get; set; } = [];

    /// <summary>LLM reasoning for the classification</summary>
    public string? Reasoning { get; set; }

    /// <summary>Whether the question is about a person (self or other)</summary>
    public bool IsPersonal => Intent is QueryIntent.PersonalSelf or QueryIntent.PersonalOther;

    /// <summary>Whether the question has a temporal component</summary>
    public bool HasTemporal => TemporalRef?.Detected ?? false;

    /// <summary>
    /// Calculate start date for temporal search
    /// </summary>
    public DateTimeOffset? GetTemporalStart(DateTimeOffset now)
    {
        if (TemporalRef == null || !TemporalRef.Detected)
            return null;

        if (TemporalRef.AbsoluteDate.HasValue)
            return TemporalRef.AbsoluteDate.Value;

        if (TemporalRef.RelativeDays.HasValue)
            return now.AddDays(TemporalRef.RelativeDays.Value);

        return null;
    }

    /// <summary>
    /// Calculate end date for temporal search
    /// </summary>
    public DateTimeOffset? GetTemporalEnd(DateTimeOffset now)
    {
        if (TemporalRef == null || !TemporalRef.Detected)
            return null;

        // For relative days, end is typically "now" or end of specified period
        if (TemporalRef.RelativeDays.HasValue)
        {
            // "вчера" means yesterday only, not yesterday to today
            var days = TemporalRef.RelativeDays.Value;
            if (days == -1) // yesterday
                return now.AddDays(-1).Date.AddDays(1).AddTicks(-1);
            if (days == -7) // last week - include up to now
                return now;
        }

        return now;
    }
}

/// <summary>
/// Entity extracted from the question
/// </summary>
public class ExtractedEntity
{
    public EntityType Type { get; set; }

    /// <summary>Extracted text value</summary>
    public required string Text { get; set; }

    /// <summary>How it was mentioned ("@username" or display name)</summary>
    public string? MentionedAs { get; set; }
}

/// <summary>
/// Temporal reference extracted from the question
/// </summary>
public class TemporalReference
{
    /// <summary>Whether a temporal reference was detected</summary>
    public bool Detected { get; set; }

    /// <summary>Original text ("вчера", "на прошлой неделе")</summary>
    public string? Text { get; set; }

    /// <summary>Type of temporal reference</summary>
    public TemporalType Type { get; set; }

    /// <summary>
    /// Relative days from now (-1 for "вчера", -7 for "неделю назад")
    /// </summary>
    public int? RelativeDays { get; set; }

    /// <summary>Absolute date if specified ("15 марта")</summary>
    public DateTimeOffset? AbsoluteDate { get; set; }
}
