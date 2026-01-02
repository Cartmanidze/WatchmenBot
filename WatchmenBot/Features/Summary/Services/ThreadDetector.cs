using WatchmenBot.Features.Summary.Models;
using WatchmenBot.Models;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Detects conversation threads and time-based activity segments.
/// Groups messages by activity bursts (>30 min gap = new segment).
/// </summary>
public class ThreadDetector(ILogger<ThreadDetector> logger)
{
    /// <summary>Gap threshold for new segment (30 minutes)</summary>
    private static readonly TimeSpan ActivityGap = TimeSpan.FromMinutes(30);

    /// <summary>Minimum messages to consider a segment significant</summary>
    private const int MinSegmentMessages = 3;

    /// <summary>Minimum reply chain depth to consider a thread</summary>
    private const int MinThreadDepth = 2;

    /// <summary>
    /// Segment messages by activity bursts (gaps > 30 min create new segments)
    /// </summary>
    public List<TimeSegment> SegmentByActivity(List<MessageRecord> messages, TimeSpan timezoneOffset)
    {
        if (messages.Count == 0)
            return [];

        var ordered = messages.OrderBy(m => m.DateUtc).ToList();
        var segments = new List<TimeSegment>();
        var currentSegment = new List<MessageRecord> { ordered[0] };

        for (var i = 1; i < ordered.Count; i++)
        {
            var gap = ordered[i].DateUtc - ordered[i - 1].DateUtc;

            if (gap > ActivityGap)
            {
                // Gap detected, close current segment
                if (currentSegment.Count >= MinSegmentMessages)
                {
                    segments.Add(CreateSegment(currentSegment, timezoneOffset));
                }
                currentSegment = [ordered[i]];
            }
            else
            {
                currentSegment.Add(ordered[i]);
            }
        }

        // Don't forget the last segment
        if (currentSegment.Count >= MinSegmentMessages)
        {
            segments.Add(CreateSegment(currentSegment, timezoneOffset));
        }

        logger.LogDebug("[ThreadDetector] Found {Count} activity segments from {Total} messages",
            segments.Count, messages.Count);

        return segments;
    }

    /// <summary>
    /// Detect reply chains and build conversation threads
    /// </summary>
    public List<ConversationThread> DetectReplyChains(List<MessageRecord> messages)
    {
        // Build lookup for quick access
        var byId = messages.ToDictionary(m => m.Id);
        var threads = new List<ConversationThread>();
        var processed = new HashSet<long>();

        foreach (var msg in messages.Where(m => m.ReplyToMessageId.HasValue))
        {
            if (processed.Contains(msg.Id))
                continue;

            // Walk up the reply chain to find root
            var chain = new List<MessageRecord>();
            var current = msg;
            var depth = 0;

            while (true)
            {
                chain.Add(current);
                processed.Add(current.Id);
                depth++;

                if (current.ReplyToMessageId.HasValue && byId.TryGetValue(current.ReplyToMessageId.Value, out var parent))
                {
                    current = parent;
                }
                else
                {
                    break;
                }
            }

            // Also find replies to this chain (walk down)
            var allInChain = new HashSet<long>(chain.Select(m => m.Id));
            var replies = messages
                .Where(m => m.ReplyToMessageId.HasValue && allInChain.Contains(m.ReplyToMessageId.Value))
                .Where(m => !allInChain.Contains(m.Id));

            foreach (var reply in replies)
            {
                chain.Add(reply);
                processed.Add(reply.Id);
            }

            // Only keep significant threads
            if (chain.Count >= MinThreadDepth)
            {
                var ordered = chain.OrderBy(m => m.DateUtc).ToList();
                threads.Add(new ConversationThread
                {
                    RootMessageId = ordered.First().Id,
                    Messages = ordered,
                    Depth = depth,
                    TimeRange = FormatTimeRange(ordered.First().DateUtc, ordered.Last().DateUtc)
                });
            }
        }

        logger.LogDebug("[ThreadDetector] Found {Count} reply chain threads", threads.Count);

        return threads;
    }

    /// <summary>
    /// Build a unified timeline from segments and threads
    /// </summary>
    public List<TimelineEntry> BuildTimeline(List<TimeSegment> segments, List<ConversationThread> threads)
    {
        var entries = new List<TimelineEntry>();

        // Add segments as timeline entries
        foreach (var segment in segments)
        {
            var topicsLabel = segment.DetectedTopics.Count > 0
                ? string.Join(", ", segment.DetectedTopics.Take(2))
                : "Общение";

            entries.Add(new TimelineEntry
            {
                TimeRange = segment.Period,
                Title = topicsLabel,
                MessageCount = segment.MessageCount,
                IsThread = false,
                Participants = segment.TopParticipants
            });
        }

        // Add significant threads as separate entries (if they span multiple segments or are large)
        foreach (var thread in threads.Where(t => t.Messages.Count >= 5))
        {
            entries.Add(new TimelineEntry
            {
                TimeRange = thread.TimeRange,
                Title = $"[Thread] {thread.InferredTopic ?? "Обсуждение"}",
                MessageCount = thread.Messages.Count,
                IsThread = true,
                Participants = GetTopParticipants(thread.Messages, 3)
            });
        }

        // Sort by start time (parse from TimeRange)
        return entries.OrderBy(e => e.TimeRange).ToList();
    }

    /// <summary>
    /// Detect "hot moments" - bursts of activity (many messages in short time)
    /// </summary>
    public List<HotMoment> DetectHotMoments(List<MessageRecord> messages, int burstThreshold = 10, TimeSpan? windowSize = null)
    {
        windowSize ??= TimeSpan.FromMinutes(5);
        var hotMoments = new List<HotMoment>();

        if (messages.Count < burstThreshold)
            return hotMoments;

        var ordered = messages.OrderBy(m => m.DateUtc).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var windowEnd = ordered[i].DateUtc + windowSize.Value;
            var messagesInWindow = ordered
                .Skip(i)
                .TakeWhile(m => m.DateUtc <= windowEnd)
                .ToList();

            if (messagesInWindow.Count >= burstThreshold)
            {
                var participants = messagesInWindow
                    .Select(m => GetDisplayName(m))
                    .Distinct()
                    .Take(5)
                    .ToList();

                hotMoments.Add(new HotMoment
                {
                    Time = ordered[i].DateUtc.ToLocalTime().ToString("HH:mm"),
                    Description = $"{messagesInWindow.Count} сообщений за {windowSize.Value.TotalMinutes} минут",
                    Participants = participants,
                    MessageBurst = messagesInWindow.Count
                });

                // Skip ahead to avoid duplicate detection
                i += messagesInWindow.Count - 1;
            }
        }

        logger.LogDebug("[ThreadDetector] Found {Count} hot moments", hotMoments.Count);

        return hotMoments.OrderByDescending(h => h.MessageBurst).Take(3).ToList();
    }

    private TimeSegment CreateSegment(List<MessageRecord> messages, TimeSpan timezoneOffset)
    {
        var first = messages.First();
        var last = messages.Last();

        var startLocal = first.DateUtc.ToOffset(timezoneOffset);
        var endLocal = last.DateUtc.ToOffset(timezoneOffset);

        return new TimeSegment
        {
            Period = $"{startLocal:HH:mm}-{endLocal:HH:mm}",
            StartTime = first.DateUtc,
            EndTime = last.DateUtc,
            Messages = messages,
            TopParticipants = GetTopParticipants(messages, 3)
        };
    }

    private static List<string> GetTopParticipants(List<MessageRecord> messages, int count)
    {
        return messages
            .GroupBy(m => GetDisplayName(m))
            .OrderByDescending(g => g.Count())
            .Take(count)
            .Select(g => g.Key)
            .ToList();
    }

    private static string GetDisplayName(MessageRecord m)
    {
        return !string.IsNullOrWhiteSpace(m.DisplayName)
            ? m.DisplayName
            : !string.IsNullOrWhiteSpace(m.Username)
                ? m.Username
                : m.FromUserId.ToString();
    }

    private static string FormatTimeRange(DateTimeOffset start, DateTimeOffset end)
    {
        return $"{start.ToLocalTime():HH:mm}-{end.ToLocalTime():HH:mm}";
    }
}
