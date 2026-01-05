namespace WatchmenBot.Infrastructure.Settings;

/// <summary>
/// Chat response mode - affects tone, style, and language of bot responses.
/// Easily extensible: just add new enum value and corresponding prompts.
/// </summary>
public enum ChatMode
{
    /// <summary>
    /// Professional, business-oriented responses.
    /// No profanity, formal tone, factual focus.
    /// Default for new chats.
    /// </summary>
    Business = 0,

    /// <summary>
    /// Casual, humorous responses with jokes and roasts.
    /// May include profanity, sarcasm, and teasing.
    /// Default for existing chats (legacy behavior).
    /// </summary>
    Funny = 1
}

/// <summary>
/// Chat language for localized prompts.
/// Prepared for future internationalization.
/// </summary>
public enum ChatLanguage
{
    /// <summary>
    /// Russian language (default)
    /// </summary>
    Ru = 0,

    /// <summary>
    /// English language (future)
    /// </summary>
    En = 1
}

/// <summary>
/// Per-chat settings including mode and language.
/// </summary>
public class ChatSettings
{
    public long ChatId { get; init; }
    public ChatMode Mode { get; init; } = ChatMode.Business;
    public ChatLanguage Language { get; init; } = ChatLanguage.Ru;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Default settings for new chats
    /// </summary>
    public static ChatSettings Default(long chatId) => new()
    {
        ChatId = chatId,
        Mode = ChatMode.Business,
        Language = ChatLanguage.Ru,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}

/// <summary>
/// Extension methods for ChatMode and ChatLanguage
/// </summary>
public static class ChatModeExtensions
{
    /// <summary>
    /// Get human-readable name for display
    /// </summary>
    public static string GetDisplayName(this ChatMode mode, ChatLanguage language = ChatLanguage.Ru)
    {
        return (mode, language) switch
        {
            (ChatMode.Business, ChatLanguage.Ru) => "Деловой",
            (ChatMode.Business, ChatLanguage.En) => "Business",
            (ChatMode.Funny, ChatLanguage.Ru) => "Весёлый",
            (ChatMode.Funny, ChatLanguage.En) => "Funny",
            _ => mode.ToString()
        };
    }

    /// <summary>
    /// Get emoji for display
    /// </summary>
    public static string GetEmoji(this ChatMode mode)
    {
        return mode switch
        {
            ChatMode.Business => "💼",
            ChatMode.Funny => "🎭",
            _ => "❓"
        };
    }

    /// <summary>
    /// Get description for display
    /// </summary>
    public static string GetDescription(this ChatMode mode, ChatLanguage language = ChatLanguage.Ru)
    {
        return (mode, language) switch
        {
            (ChatMode.Business, ChatLanguage.Ru) => "Профессиональные ответы без мата и подколок",
            (ChatMode.Business, ChatLanguage.En) => "Professional responses without profanity",
            (ChatMode.Funny, ChatLanguage.Ru) => "Дерзкие ответы с юмором, сарказмом и матом",
            (ChatMode.Funny, ChatLanguage.En) => "Edgy responses with humor and sarcasm",
            _ => ""
        };
    }

    /// <summary>
    /// Get prompt key suffix for this mode
    /// </summary>
    public static string ToPromptKey(this ChatMode mode)
    {
        return mode switch
        {
            ChatMode.Business => "business",
            ChatMode.Funny => "funny",
            _ => "business"
        };
    }

    /// <summary>
    /// Get prompt key suffix for this language
    /// </summary>
    public static string ToPromptKey(this ChatLanguage language)
    {
        return language switch
        {
            ChatLanguage.Ru => "ru",
            ChatLanguage.En => "en",
            _ => "ru"
        };
    }

    /// <summary>
    /// Parse mode from string (case-insensitive)
    /// </summary>
    public static bool TryParse(string? value, out ChatMode mode)
    {
        mode = ChatMode.Business;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().ToLowerInvariant();

        // Support both English and Russian names
        mode = normalized switch
        {
            "business" or "бизнес" or "деловой" or "0" => ChatMode.Business,
            "funny" or "fun" or "весёлый" or "веселый" or "смешной" or "1" => ChatMode.Funny,
            _ => ChatMode.Business
        };

        return normalized is "business" or "бизнес" or "деловой" or "0"
            or "funny" or "fun" or "весёлый" or "веселый" or "смешной" or "1";
    }
}