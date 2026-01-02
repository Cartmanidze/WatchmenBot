using WatchmenBot.Models;

namespace WatchmenBot.Features.Summary.Models;

// ============================================
// Timeline Models (ThreadDetector output)
// ============================================

/// <summary>
/// A segment of messages grouped by activity (gaps > 30 min create new segments)
/// </summary>
public class TimeSegment
{
    /// <summary>Time range in local time, e.g. "09:00-12:00"</summary>
    public string Period { get; set; } = "";

    /// <summary>Start time of the segment</summary>
    public DateTimeOffset StartTime { get; set; }

    /// <summary>End time of the segment</summary>
    public DateTimeOffset EndTime { get; set; }

    /// <summary>Messages in this segment</summary>
    public List<MessageRecord> Messages { get; set; } = [];

    /// <summary>Number of messages</summary>
    public int MessageCount => Messages.Count;

    /// <summary>Top participants by message count</summary>
    public List<string> TopParticipants { get; set; } = [];

    /// <summary>LLM-detected topics for this segment</summary>
    public List<string> DetectedTopics { get; set; } = [];
}

/// <summary>
/// A conversation thread detected via reply chains
/// </summary>
public class ConversationThread
{
    /// <summary>Root message ID (first in the chain)</summary>
    public long RootMessageId { get; set; }

    /// <summary>Messages in the thread, ordered chronologically</summary>
    public List<MessageRecord> Messages { get; set; } = [];

    /// <summary>Reply chain depth (how many levels deep)</summary>
    public int Depth { get; set; }

    /// <summary>LLM-inferred topic of the thread</summary>
    public string? InferredTopic { get; set; }

    /// <summary>Time range of the thread</summary>
    public string TimeRange { get; set; } = "";
}

/// <summary>
/// A timeline entry for the summary (either a time segment or a thread)
/// </summary>
public class TimelineEntry
{
    /// <summary>Time range, e.g. "09:00-11:30"</summary>
    public string TimeRange { get; set; } = "";

    /// <summary>Title/description, e.g. "Утренний стендап" or "[Thread: Баги в проде]"</summary>
    public string Title { get; set; } = "";

    /// <summary>Number of messages in this entry</summary>
    public int MessageCount { get; set; }

    /// <summary>True if this is a reply chain thread, false if time segment</summary>
    public bool IsThread { get; set; }

    /// <summary>Top participants in this entry</summary>
    public List<string> Participants { get; set; } = [];
}

// ============================================
// Event Models (EventDetector output)
// ============================================

/// <summary>
/// Container for extracted events, decisions, and questions
/// </summary>
public class ExtractedEvents
{
    public List<KeyEvent> Events { get; set; } = [];
    public List<Decision> Decisions { get; set; } = [];
    public List<OpenQuestion> OpenQuestions { get; set; } = [];
}

/// <summary>
/// A key event that happened in the chat
/// </summary>
public class KeyEvent
{
    /// <summary>When it happened, e.g. "14:23"</summary>
    public string? Time { get; set; }

    /// <summary>What happened</summary>
    public string Description { get; set; } = "";

    /// <summary>Who was involved</summary>
    public List<string> Participants { get; set; } = [];

    /// <summary>Importance level: "critical", "notable", "minor"</summary>
    public string Importance { get; set; } = "notable";
}

/// <summary>
/// A decision made by the group
/// </summary>
public class Decision
{
    /// <summary>What was decided</summary>
    public string What { get; set; } = "";

    /// <summary>Who made/proposed the decision</summary>
    public string? Who { get; set; }

    /// <summary>When it was decided</summary>
    public string? When { get; set; }
}

/// <summary>
/// An unresolved question from the chat
/// </summary>
public class OpenQuestion
{
    /// <summary>The question itself</summary>
    public string Question { get; set; } = "";

    /// <summary>Context around the question</summary>
    public string? Context { get; set; }
}

// ============================================
// Quote Models (QuoteMiner output)
// ============================================

/// <summary>
/// Container for mined quotes and hot moments
/// </summary>
public class MinedQuotes
{
    public List<MemoableQuote> BestQuotes { get; set; } = [];
    public List<HotMoment> HotMoments { get; set; } = [];
}

/// <summary>
/// A memorable quote from the chat
/// </summary>
public class MemoableQuote
{
    /// <summary>The quote text (exact)</summary>
    public string Text { get; set; } = "";

    /// <summary>Who said it</summary>
    public string Author { get; set; } = "";

    /// <summary>What it was about</summary>
    public string? Context { get; set; }

    /// <summary>Category: "funny", "wise", "savage", "wholesome"</summary>
    public string Category { get; set; } = "funny";
}

/// <summary>
/// A hot/heated moment in the chat
/// </summary>
public class HotMoment
{
    /// <summary>When it happened</summary>
    public string? Time { get; set; }

    /// <summary>What happened</summary>
    public string Description { get; set; } = "";

    /// <summary>Who was involved</summary>
    public List<string> Participants { get; set; } = [];

    /// <summary>Message count in short time (indicator of intensity)</summary>
    public int MessageBurst { get; set; }
}

// ============================================
// Participant Models
// ============================================

/// <summary>
/// Activity summary for a participant
/// </summary>
public class ParticipantActivity
{
    /// <summary>Display name or username</summary>
    public string Name { get; set; } = "";

    /// <summary>Total messages sent</summary>
    public int MessageCount { get; set; }

    /// <summary>Time periods when active, e.g. ["09:00-12:00", "14:00-17:00"]</summary>
    public List<string> ActivePeriods { get; set; } = [];

    /// <summary>Topics they engaged in</summary>
    public List<string> TopicsEngaged { get; set; } = [];
}

// ============================================
// Enhanced Facts (Stage 1 output)
// ============================================

/// <summary>
/// Enhanced extracted facts including timeline, events, quotes
/// </summary>
public class EnhancedExtractedFacts
{
    public List<TimelineFact> Timeline { get; set; } = [];
    public List<KeyEvent> Events { get; set; } = [];
    public List<Decision> Decisions { get; set; } = [];
    public List<MemoableQuote> Quotes { get; set; } = [];
    public List<HeroFact> Heroes { get; set; } = [];
    public List<HotMoment> HotMoments { get; set; } = [];
    public List<OpenQuestion> OpenQuestions { get; set; } = [];
}

/// <summary>
/// A timeline entry from LLM extraction
/// </summary>
public class TimelineFact
{
    public string Period { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> Topics { get; set; } = [];
    public int MessageCount { get; set; }
}

/// <summary>
/// Hero of the day
/// </summary>
public class HeroFact
{
    public string Name { get; set; } = "";
    public string Why { get; set; } = "";
    public string? Achievement { get; set; }
}
