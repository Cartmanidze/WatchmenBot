namespace WatchmenBot.Features.Memory.Models;

#region Public Models

public class UserProfile
{
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public string? DisplayName { get; set; }
    public string? Username { get; set; }
    public List<string> Facts { get; set; } = [];
    public List<string> Traits { get; set; } = [];
    public List<string> Interests { get; set; } = [];
    public List<string> NotableQuotes { get; set; } = [];
    public int InteractionCount { get; set; }
    public DateTimeOffset? LastInteraction { get; set; }
}

public class EnhancedProfile
{
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public string? DisplayName { get; set; }
    public string? Username { get; set; }
    public int MessageCount { get; set; }
    public string? Summary { get; set; }
    public string? CommunicationStyle { get; set; }
    public string? RoleInChat { get; set; }
    public List<string> Interests { get; set; } = [];
    public List<string> Traits { get; set; } = [];
    public List<string> RoastMaterial { get; set; } = [];
}

public class UserFact
{
    public string FactType { get; set; } = "";
    public string FactText { get; set; } = "";
    public double Confidence { get; set; }
}

public class ConversationMemory
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public string Query { get; set; } = "";
    public string ResponseSummary { get; set; } = "";
    public List<string> Topics { get; set; } = [];
    public List<string> ExtractedFacts { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public class ProfileExtraction
{
    public List<string> Facts { get; set; } = [];
    public List<string> Traits { get; set; } = [];
    public List<string> Interests { get; set; } = [];
    public List<string> Quotes { get; set; } = [];
}

public class MemorySummary
{
    public string Summary { get; set; } = "";
    public List<string> Topics { get; set; } = [];
    public List<string> Facts { get; set; } = [];
}

#endregion

#region Internal Database Records (for Dapper)

internal class UserProfileRecord
{
    public long user_id { get; set; }
    public long chat_id { get; set; }
    public string? display_name { get; set; }
    public string? username { get; set; }
    public string? facts { get; set; }
    public string? traits { get; set; }
    public string? interests { get; set; }
    public string? notable_quotes { get; set; }
    public int interaction_count { get; set; }
    public DateTime? last_interaction { get; set; }
}

internal class ConversationMemoryRecord
{
    public long id { get; set; }
    public long user_id { get; set; }
    public long chat_id { get; set; }
    public string query { get; set; } = "";
    public string response_summary { get; set; } = "";
    public string? topics { get; set; }
    public string? extracted_facts { get; set; }
    public DateTime created_at { get; set; }
}

internal class EnhancedProfileRecord
{
    public long user_id { get; set; }
    public long chat_id { get; set; }
    public string? display_name { get; set; }
    public string? username { get; set; }
    public int message_count { get; set; }
    public string? summary { get; set; }
    public string? communication_style { get; set; }
    public string? role_in_chat { get; set; }
    public string? interests { get; set; }
    public string? traits { get; set; }
    public string? roast_material { get; set; }
}

internal class UserFactRecord
{
    public string fact_type { get; set; } = "";
    public string fact_text { get; set; } = "";
    public double confidence { get; set; }
}

#endregion
