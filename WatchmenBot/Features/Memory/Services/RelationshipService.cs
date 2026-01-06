using Dapper;
using WatchmenBot.Features.Memory.Models;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Memory.Services;

/// <summary>
/// Service for managing user relationships (spouse, sibling, friend, etc.)
/// </summary>
public class RelationshipService(
    IDbConnectionFactory connectionFactory,
    ILogger<RelationshipService> logger)
{
    /// <summary>
    /// Get all active relationships for a user in a chat
    /// </summary>
    public async Task<List<UserRelationship>> GetUserRelationshipsAsync(
        long chatId, long userId, double minConfidence = 0.0)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var records = await connection.QueryAsync<UserRelationshipRecord>(
                """
                SELECT id, chat_id, user_id, related_user_id, related_person_name,
                       relationship_type, relationship_label, confidence, mention_count,
                       source_message_ids, is_active, first_seen, last_seen, ended_at, end_reason
                FROM user_relationships
                WHERE chat_id = @ChatId AND user_id = @UserId AND is_active = TRUE
                  AND confidence >= @MinConfidence
                ORDER BY confidence DESC, mention_count DESC
                """,
                new { ChatId = chatId, UserId = userId, MinConfidence = minConfidence });

            return records.Select(MapToModel).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Relationships] Failed to get relationships for user {UserId}", userId);
            return [];
        }
    }

    /// <summary>
    /// Upsert a relationship (insert or update mention_count and confidence)
    /// </summary>
    public async Task UpsertRelationshipAsync(
        long chatId,
        long userId,
        string relatedPersonName,
        string relationshipType,
        string? relationshipLabel,
        double confidence,
        long? sourceMessageId = null,
        long? relatedUserId = null)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Check if there's an existing relationship of the same type but different person
            // If so, we might need to end the old one
            if (relationshipType is "spouse" or "partner")
            {
                await EndExistingRelationshipIfDifferentAsync(
                    connection, chatId, userId, relatedPersonName, relationshipType);
            }

            var sourceIds = sourceMessageId.HasValue
                ? new[] { sourceMessageId.Value }
                : Array.Empty<long>();

            await connection.ExecuteAsync(
                """
                INSERT INTO user_relationships
                    (chat_id, user_id, related_user_id, related_person_name, relationship_type,
                     relationship_label, confidence, source_message_ids)
                VALUES
                    (@ChatId, @UserId, @RelatedUserId, @RelatedPersonName, @RelationshipType,
                     @RelationshipLabel, @Confidence, @SourceMessageIds)
                ON CONFLICT (chat_id, user_id, related_person_name, relationship_type)
                DO UPDATE SET
                    mention_count = user_relationships.mention_count + 1,
                    confidence = GREATEST(user_relationships.confidence, EXCLUDED.confidence),
                    last_seen = NOW(),
                    source_message_ids = CASE
                        WHEN array_length(EXCLUDED.source_message_ids, 1) > 0
                        THEN user_relationships.source_message_ids || EXCLUDED.source_message_ids
                        ELSE user_relationships.source_message_ids
                    END,
                    related_user_id = COALESCE(EXCLUDED.related_user_id, user_relationships.related_user_id)
                """,
                new
                {
                    ChatId = chatId,
                    UserId = userId,
                    RelatedUserId = relatedUserId,
                    RelatedPersonName = relatedPersonName,
                    RelationshipType = relationshipType,
                    RelationshipLabel = relationshipLabel,
                    Confidence = confidence,
                    SourceMessageIds = sourceIds
                });

            logger.LogDebug("[Relationships] Upserted {Type} relationship: {User} → {Person}",
                relationshipType, userId, relatedPersonName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Relationships] Failed to upsert relationship for user {UserId}", userId);
        }
    }

    /// <summary>
    /// End existing relationship of same type if person name is different (for exclusive relationships like spouse)
    /// </summary>
    private async Task EndExistingRelationshipIfDifferentAsync(
        System.Data.IDbConnection connection,
        long chatId,
        long userId,
        string newPersonName,
        string relationshipType)
    {
        var existing = await connection.QueryFirstOrDefaultAsync<UserRelationshipRecord>(
            """
            SELECT id, related_person_name
            FROM user_relationships
            WHERE chat_id = @ChatId AND user_id = @UserId
              AND relationship_type = @Type AND is_active = TRUE
            """,
            new { ChatId = chatId, UserId = userId, Type = relationshipType });

        if (existing != null &&
            !existing.related_person_name.Equals(newPersonName, StringComparison.OrdinalIgnoreCase))
        {
            await connection.ExecuteAsync(
                """
                UPDATE user_relationships
                SET is_active = FALSE, ended_at = NOW(), end_reason = 'updated'
                WHERE id = @Id
                """,
                new { Id = existing.id });

            logger.LogInformation("[Relationships] Ended previous {Type} relationship with {Person} (replaced)",
                relationshipType, existing.related_person_name);
        }
    }

    private static UserRelationship MapToModel(UserRelationshipRecord r) => new()
    {
        Id = r.id,
        ChatId = r.chat_id,
        UserId = r.user_id,
        RelatedUserId = r.related_user_id,
        RelatedPersonName = r.related_person_name,
        RelationshipType = r.relationship_type,
        RelationshipLabel = r.relationship_label,
        Confidence = r.confidence,
        MentionCount = r.mention_count,
        SourceMessageIds = r.source_message_ids,
        IsActive = r.is_active,
        FirstSeen = new DateTimeOffset(r.first_seen, TimeSpan.Zero),
        LastSeen = new DateTimeOffset(r.last_seen, TimeSpan.Zero),
        EndedAt = r.ended_at.HasValue ? new DateTimeOffset(r.ended_at.Value, TimeSpan.Zero) : null,
        EndReason = r.end_reason
    };
}
