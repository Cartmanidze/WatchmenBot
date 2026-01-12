using Dapper;

namespace WatchmenBot.Infrastructure.Database;

public class DatabaseInitializer(
    IDbConnectionFactory connectionFactory,
    ILogger<DatabaseInitializer> logger,
    IConfiguration configuration)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Enable pgvector extension
            await EnablePgVectorAsync(connection);

            // Create tables
            await CreateMessagesTableAsync(connection);
            await CreateChatsTableAsync(connection);
            await CreateEmbeddingsTableAsync(connection);
            await CreateAdminSettingsTableAsync(connection);
            await CreatePromptSettingsTableAsync(connection);
            await CreateUserProfilesTableAsync(connection);
            await CreateConversationMemoryTableAsync(connection);
            await CreateMessageQueueTableAsync(connection);
            await CreateAskQueueTableAsync(connection);
            await CreateSummaryQueueTableAsync(connection);
            await CreateTruthQueueTableAsync(connection);
            await CreateQueueNotifyTriggersAsync(connection);
            await CreateUserFactsTableAsync(connection);
            await CreateContextEmbeddingsTableAsync(connection);
            await CreateChatSettingsTableAsync(connection);
            await CreateUserAliasesTableAsync(connection);
            await CreateUserRelationshipsTableAsync(connection);

            // Create indexes
            await CreateIndexesAsync(connection);

            logger.LogInformation("Database initialized successfully with pgvector support");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize database");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnablePgVectorAsync(System.Data.IDbConnection connection)
    {
        try
        {
            await connection.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector;");
            logger.LogInformation("pgvector extension enabled");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not enable pgvector extension. RAG features will be disabled. " +
                                   "Install pgvector: https://github.com/pgvector/pgvector");
        }
    }

    private static async Task CreateMessagesTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS messages (
                id BIGINT NOT NULL,
                chat_id BIGINT NOT NULL,
                thread_id BIGINT NULL,
                from_user_id BIGINT NOT NULL,
                username VARCHAR(255) NULL,
                display_name VARCHAR(255) NULL,
                text TEXT NULL,
                date_utc TIMESTAMP WITH TIME ZONE NOT NULL,
                has_links BOOLEAN NOT NULL DEFAULT FALSE,
                has_media BOOLEAN NOT NULL DEFAULT FALSE,
                reply_to_message_id BIGINT NULL,
                message_type VARCHAR(50) NOT NULL DEFAULT 'text',
                created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                PRIMARY KEY (chat_id, id)
            );
            """;

        await connection.ExecuteAsync(createTableSql);

        // Add forward-related columns (migration for existing databases)
        const string addForwardColumnsSql = """
            ALTER TABLE messages ADD COLUMN IF NOT EXISTS is_forwarded BOOLEAN DEFAULT FALSE;
            ALTER TABLE messages ADD COLUMN IF NOT EXISTS forward_origin_type VARCHAR(20);
            ALTER TABLE messages ADD COLUMN IF NOT EXISTS forward_from_name VARCHAR(255);
            ALTER TABLE messages ADD COLUMN IF NOT EXISTS forward_from_id BIGINT;
            ALTER TABLE messages ADD COLUMN IF NOT EXISTS forward_date TIMESTAMPTZ;
            """;

        await connection.ExecuteAsync(addForwardColumnsSql);
    }

    private static async Task CreateChatsTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS chats (
                id BIGINT PRIMARY KEY,
                title VARCHAR(255) NULL,
                type VARCHAR(50) NOT NULL DEFAULT 'group',
                updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
            );
            """;

        await connection.ExecuteAsync(createTableSql);
    }

    private async Task CreateEmbeddingsTableAsync(System.Data.IDbConnection connection)
    {
        var dimensions = configuration.GetValue("Embeddings:Dimensions", 1536);

        try
        {
            // Check if table exists and has correct dimensions
            var checkDimensionsSql = """
                SELECT atttypmod
                FROM pg_attribute a
                JOIN pg_class c ON a.attrelid = c.oid
                WHERE c.relname = 'message_embeddings'
                  AND a.attname = 'embedding'
                  AND a.atttypid = (SELECT oid FROM pg_type WHERE typname = 'vector');
                """;

            var currentDimensions = await connection.QueryFirstOrDefaultAsync<int?>(checkDimensionsSql);

            if (currentDimensions.HasValue && currentDimensions.Value != dimensions)
            {
                logger.LogWarning(
                    "Embedding dimensions changed from {OldDim} to {NewDim}. Recreating table (all embeddings will be lost!)",
                    currentDimensions.Value, dimensions);

                // Drop old table and indexes
                await connection.ExecuteAsync("DROP TABLE IF EXISTS message_embeddings CASCADE;");
                logger.LogInformation("Old message_embeddings table dropped");
            }

            var createTableSql = $"""
                CREATE TABLE IF NOT EXISTS message_embeddings (
                    id BIGSERIAL PRIMARY KEY,
                    chat_id BIGINT NOT NULL,
                    message_id BIGINT NOT NULL,
                    chunk_index INT NOT NULL DEFAULT 0,
                    chunk_text TEXT NOT NULL,
                    embedding vector({dimensions}),
                    metadata JSONB,
                    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                    UNIQUE(chat_id, message_id, chunk_index)
                );
                """;

            await connection.ExecuteAsync(createTableSql);

            // Add is_question column for Q→A semantic bridge (generated questions linked to answers)
            const string addIsQuestionColumnSql = """
                ALTER TABLE message_embeddings ADD COLUMN IF NOT EXISTS is_question BOOLEAN DEFAULT FALSE;
                ALTER TABLE message_embeddings ADD COLUMN IF NOT EXISTS source_message_id BIGINT;
                """;
            await connection.ExecuteAsync(addIsQuestionColumnSql);

            logger.LogInformation("Embeddings table ready with {Dimensions} dimensions", dimensions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create embeddings table. pgvector may not be installed");
        }
    }

    private static async Task CreateAdminSettingsTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS admin_settings (
                key VARCHAR(100) PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
            );
            """;

        await connection.ExecuteAsync(createTableSql);
    }

    private static async Task CreatePromptSettingsTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS prompt_settings (
                command VARCHAR(50) PRIMARY KEY,
                description VARCHAR(255) NOT NULL,
                system_prompt TEXT NOT NULL,
                llm_tag VARCHAR(50) NULL,
                updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
            );
            """;

        await connection.ExecuteAsync(createTableSql);

        // Add llm_tag column if it doesn't exist (migration for existing databases)
        const string addColumnSql = """
            ALTER TABLE prompt_settings ADD COLUMN IF NOT EXISTS llm_tag VARCHAR(50) NULL;
            """;

        await connection.ExecuteAsync(addColumnSql);
    }

    private static async Task CreateUserProfilesTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS user_profiles (
                user_id BIGINT NOT NULL,
                chat_id BIGINT NOT NULL,
                display_name VARCHAR(255) NULL,
                username VARCHAR(255) NULL,
                facts JSONB NOT NULL DEFAULT '[]'::jsonb,
                traits JSONB NOT NULL DEFAULT '[]'::jsonb,
                interests JSONB NOT NULL DEFAULT '[]'::jsonb,
                notable_quotes JSONB NOT NULL DEFAULT '[]'::jsonb,
                interaction_count INT NOT NULL DEFAULT 0,
                last_interaction TIMESTAMP WITH TIME ZONE NULL,
                created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                PRIMARY KEY (chat_id, user_id)
            );
            """;

        await connection.ExecuteAsync(createTableSql);

        // Add new columns for hybrid profile system
        const string addColumnsSql = """
            ALTER TABLE user_profiles ADD COLUMN IF NOT EXISTS message_count INT DEFAULT 0;
            ALTER TABLE user_profiles ADD COLUMN IF NOT EXISTS last_message_at TIMESTAMPTZ;
            ALTER TABLE user_profiles ADD COLUMN IF NOT EXISTS active_hours JSONB DEFAULT '{}';
            ALTER TABLE user_profiles ADD COLUMN IF NOT EXISTS summary TEXT;
            ALTER TABLE user_profiles ADD COLUMN IF NOT EXISTS communication_style TEXT;
            ALTER TABLE user_profiles ADD COLUMN IF NOT EXISTS role_in_chat TEXT;
            ALTER TABLE user_profiles ADD COLUMN IF NOT EXISTS roast_material JSONB DEFAULT '[]';
            ALTER TABLE user_profiles ADD COLUMN IF NOT EXISTS profile_version INT DEFAULT 1;
            ALTER TABLE user_profiles ADD COLUMN IF NOT EXISTS last_profile_update TIMESTAMPTZ;
            ALTER TABLE user_profiles ADD COLUMN IF NOT EXISTS gender VARCHAR(10);
            ALTER TABLE user_profiles ADD COLUMN IF NOT EXISTS gender_confidence FLOAT DEFAULT 0;
            """;

        await connection.ExecuteAsync(addColumnsSql);
    }

    private static async Task CreateConversationMemoryTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS conversation_memory (
                id BIGSERIAL PRIMARY KEY,
                user_id BIGINT NOT NULL,
                chat_id BIGINT NOT NULL,
                query TEXT NOT NULL,
                response_summary TEXT NOT NULL,
                topics JSONB NOT NULL DEFAULT '[]'::jsonb,
                extracted_facts JSONB NOT NULL DEFAULT '[]'::jsonb,
                created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_conv_memory_user_chat
            ON conversation_memory (chat_id, user_id, created_at DESC);
            """;

        await connection.ExecuteAsync(createTableSql);
    }

    private static async Task CreateMessageQueueTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS message_queue (
                id SERIAL PRIMARY KEY,
                chat_id BIGINT NOT NULL,
                message_id BIGINT NOT NULL,
                user_id BIGINT NOT NULL,
                display_name TEXT,
                text TEXT NOT NULL,
                created_at TIMESTAMPTZ DEFAULT NOW(),
                processed BOOLEAN DEFAULT FALSE,
                UNIQUE(chat_id, message_id)
            );

            CREATE INDEX IF NOT EXISTS idx_message_queue_pending
            ON message_queue (processed, created_at)
            WHERE processed = FALSE;
            """;

        await connection.ExecuteAsync(createTableSql);
    }

    private static async Task CreateAskQueueTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS ask_queue (
                id SERIAL PRIMARY KEY,
                chat_id BIGINT NOT NULL,
                reply_to_message_id INT NOT NULL,
                question TEXT NOT NULL,
                command VARCHAR(20) NOT NULL DEFAULT 'ask',
                asker_id BIGINT NOT NULL,
                asker_name TEXT NOT NULL,
                asker_username TEXT,
                created_at TIMESTAMPTZ DEFAULT NOW(),
                processed BOOLEAN DEFAULT FALSE,
                started_at TIMESTAMPTZ,
                completed_at TIMESTAMPTZ,
                error TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_ask_queue_pending
            ON ask_queue (processed, created_at)
            WHERE processed = FALSE;
            """;

        await connection.ExecuteAsync(createTableSql);

        // Migration: add retry and lease columns for robust queue processing
        const string addRetryColumnsSql = """
            ALTER TABLE ask_queue ADD COLUMN IF NOT EXISTS attempt_count INT DEFAULT 0;
            ALTER TABLE ask_queue ADD COLUMN IF NOT EXISTS max_attempts INT DEFAULT 3;
            ALTER TABLE ask_queue ADD COLUMN IF NOT EXISTS next_run_at TIMESTAMPTZ DEFAULT NOW();
            ALTER TABLE ask_queue ADD COLUMN IF NOT EXISTS picked_at TIMESTAMPTZ;
            ALTER TABLE ask_queue ADD COLUMN IF NOT EXISTS idempotency_key VARCHAR(64);
            """;
        await connection.ExecuteAsync(addRetryColumnsSql);

        // Index for efficient pending query with retry support
        // Note: Cannot use NOW() in partial index predicate (not IMMUTABLE)
        // The time-based condition is evaluated at query time instead
        const string addRetryIndexSql = """
            CREATE INDEX IF NOT EXISTS idx_ask_queue_ready
            ON ask_queue (next_run_at)
            WHERE processed = FALSE;
            """;
        await connection.ExecuteAsync(addRetryIndexSql);

        // Unique partial index for idempotency (only for non-processed items)
        const string addIdempotencyIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_ask_queue_idempotency
            ON ask_queue (idempotency_key)
            WHERE processed = FALSE AND idempotency_key IS NOT NULL;
            """;
        await connection.ExecuteAsync(addIdempotencyIndexSql);
    }

    private static async Task CreateSummaryQueueTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS summary_queue (
                id SERIAL PRIMARY KEY,
                chat_id BIGINT NOT NULL,
                reply_to_message_id INT NOT NULL,
                hours INT NOT NULL DEFAULT 24,
                requested_by TEXT NOT NULL,
                created_at TIMESTAMPTZ DEFAULT NOW(),
                processed BOOLEAN DEFAULT FALSE,
                started_at TIMESTAMPTZ,
                completed_at TIMESTAMPTZ,
                error TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_summary_queue_pending
            ON summary_queue (processed, created_at)
            WHERE processed = FALSE;
            """;

        await connection.ExecuteAsync(createTableSql);

        // Migration: add retry and lease columns
        const string addRetryColumnsSql = """
            ALTER TABLE summary_queue ADD COLUMN IF NOT EXISTS attempt_count INT DEFAULT 0;
            ALTER TABLE summary_queue ADD COLUMN IF NOT EXISTS max_attempts INT DEFAULT 3;
            ALTER TABLE summary_queue ADD COLUMN IF NOT EXISTS next_run_at TIMESTAMPTZ DEFAULT NOW();
            ALTER TABLE summary_queue ADD COLUMN IF NOT EXISTS picked_at TIMESTAMPTZ;
            ALTER TABLE summary_queue ADD COLUMN IF NOT EXISTS idempotency_key VARCHAR(64);
            """;
        await connection.ExecuteAsync(addRetryColumnsSql);

        // Note: Cannot use NOW() in partial index predicate (not IMMUTABLE)
        const string addRetryIndexSql = """
            CREATE INDEX IF NOT EXISTS idx_summary_queue_ready
            ON summary_queue (next_run_at)
            WHERE processed = FALSE;
            """;
        await connection.ExecuteAsync(addRetryIndexSql);
    }

    private static async Task CreateTruthQueueTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS truth_queue (
                id SERIAL PRIMARY KEY,
                chat_id BIGINT NOT NULL,
                reply_to_message_id INT NOT NULL,
                message_count INT NOT NULL DEFAULT 5,
                requested_by TEXT NOT NULL,
                created_at TIMESTAMPTZ DEFAULT NOW(),
                processed BOOLEAN DEFAULT FALSE,
                started_at TIMESTAMPTZ,
                completed_at TIMESTAMPTZ,
                error TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_truth_queue_pending
            ON truth_queue (processed, created_at)
            WHERE processed = FALSE;
            """;

        await connection.ExecuteAsync(createTableSql);

        // Migration: add retry and lease columns
        const string addRetryColumnsSql = """
            ALTER TABLE truth_queue ADD COLUMN IF NOT EXISTS attempt_count INT DEFAULT 0;
            ALTER TABLE truth_queue ADD COLUMN IF NOT EXISTS max_attempts INT DEFAULT 3;
            ALTER TABLE truth_queue ADD COLUMN IF NOT EXISTS next_run_at TIMESTAMPTZ DEFAULT NOW();
            ALTER TABLE truth_queue ADD COLUMN IF NOT EXISTS picked_at TIMESTAMPTZ;
            ALTER TABLE truth_queue ADD COLUMN IF NOT EXISTS idempotency_key VARCHAR(64);
            """;
        await connection.ExecuteAsync(addRetryColumnsSql);

        // Note: Cannot use NOW() in partial index predicate (not IMMUTABLE)
        const string addRetryIndexSql = """
            CREATE INDEX IF NOT EXISTS idx_truth_queue_ready
            ON truth_queue (next_run_at)
            WHERE processed = FALSE;
            """;
        await connection.ExecuteAsync(addRetryIndexSql);
    }

    private async Task CreateQueueNotifyTriggersAsync(System.Data.IDbConnection connection)
    {
        // Create NOTIFY triggers for real-time queue processing
        // Workers will LISTEN to these channels instead of polling
        const string createTriggersSql = """
            -- Function to notify on new ask_queue item
            CREATE OR REPLACE FUNCTION notify_ask_queue_insert()
            RETURNS TRIGGER AS $$
            BEGIN
                PERFORM pg_notify('ask_queue_channel', NEW.id::text);
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            -- Function to notify on new summary_queue item
            CREATE OR REPLACE FUNCTION notify_summary_queue_insert()
            RETURNS TRIGGER AS $$
            BEGIN
                PERFORM pg_notify('summary_queue_channel', NEW.id::text);
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            -- Function to notify on new truth_queue item
            CREATE OR REPLACE FUNCTION notify_truth_queue_insert()
            RETURNS TRIGGER AS $$
            BEGIN
                PERFORM pg_notify('truth_queue_channel', NEW.id::text);
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            -- Trigger for ask_queue (drop first to avoid duplicates)
            DROP TRIGGER IF EXISTS ask_queue_notify_trigger ON ask_queue;
            CREATE TRIGGER ask_queue_notify_trigger
            AFTER INSERT ON ask_queue
            FOR EACH ROW EXECUTE FUNCTION notify_ask_queue_insert();

            -- Trigger for summary_queue (drop first to avoid duplicates)
            DROP TRIGGER IF EXISTS summary_queue_notify_trigger ON summary_queue;
            CREATE TRIGGER summary_queue_notify_trigger
            AFTER INSERT ON summary_queue
            FOR EACH ROW EXECUTE FUNCTION notify_summary_queue_insert();

            -- Trigger for truth_queue (drop first to avoid duplicates)
            DROP TRIGGER IF EXISTS truth_queue_notify_trigger ON truth_queue;
            CREATE TRIGGER truth_queue_notify_trigger
            AFTER INSERT ON truth_queue
            FOR EACH ROW EXECUTE FUNCTION notify_truth_queue_insert();
            """;

        await connection.ExecuteAsync(createTriggersSql);
        logger.LogInformation("Queue NOTIFY triggers created for real-time processing");
    }

    private static async Task CreateUserFactsTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS user_facts (
                id SERIAL PRIMARY KEY,
                user_id BIGINT NOT NULL,
                chat_id BIGINT NOT NULL,
                fact_type TEXT NOT NULL,
                fact_text TEXT NOT NULL,
                confidence FLOAT DEFAULT 0.7,
                source_message_ids BIGINT[],
                created_at TIMESTAMPTZ DEFAULT NOW(),
                expires_at TIMESTAMPTZ,
                UNIQUE(chat_id, user_id, fact_text)
            );

            CREATE INDEX IF NOT EXISTS idx_user_facts_lookup
            ON user_facts (chat_id, user_id);
            """;

        await connection.ExecuteAsync(createTableSql);
    }

    private async Task CreateContextEmbeddingsTableAsync(System.Data.IDbConnection connection)
    {
        var dimensions = configuration.GetValue("Embeddings:Dimensions", 1536);

        try
        {
            // Check if table exists and has correct dimensions
            var checkDimensionsSql = """
                SELECT atttypmod
                FROM pg_attribute a
                JOIN pg_class c ON a.attrelid = c.oid
                WHERE c.relname = 'context_embeddings'
                  AND a.attname = 'embedding'
                  AND a.atttypid = (SELECT oid FROM pg_type WHERE typname = 'vector');
                """;

            var currentDimensions = await connection.QueryFirstOrDefaultAsync<int?>(checkDimensionsSql);

            if (currentDimensions.HasValue && currentDimensions.Value != dimensions)
            {
                logger.LogWarning(
                    "Context embedding dimensions changed from {OldDim} to {NewDim}. Recreating table.",
                    currentDimensions.Value, dimensions);

                await connection.ExecuteAsync("DROP TABLE IF EXISTS context_embeddings CASCADE;");
            }

            var createTableSql = $"""
                CREATE TABLE IF NOT EXISTS context_embeddings (
                    id BIGSERIAL PRIMARY KEY,
                    chat_id BIGINT NOT NULL,
                    center_message_id BIGINT NOT NULL,
                    window_start_id BIGINT NOT NULL,
                    window_end_id BIGINT NOT NULL,
                    message_ids BIGINT[] NOT NULL,
                    context_text TEXT NOT NULL,
                    embedding vector({dimensions}),
                    window_size INT NOT NULL DEFAULT 10,
                    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                    UNIQUE(chat_id, center_message_id)
                );
                """;

            await connection.ExecuteAsync(createTableSql);
            logger.LogInformation("Context embeddings table ready with {Dimensions} dimensions", dimensions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create context_embeddings table");
        }
    }

    private async Task CreateChatSettingsTableAsync(System.Data.IDbConnection connection)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS chat_settings (
                chat_id BIGINT PRIMARY KEY,
                mode SMALLINT NOT NULL DEFAULT 0,
                language SMALLINT NOT NULL DEFAULT 0,
                created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
            );
            """;

        await connection.ExecuteAsync(createTableSql);

        // Migration: set existing chats to 'funny' mode (mode=1)
        // New chats will get 'business' mode (mode=0) by default
        // Note: chats table uses 'id' as primary key, not 'chat_id'
        const string migrationSql = """
            INSERT INTO chat_settings (chat_id, mode, language)
            SELECT DISTINCT id, 1, 0
            FROM chats
            WHERE id NOT IN (SELECT chat_id FROM chat_settings)
            ON CONFLICT (chat_id) DO NOTHING;
            """;

        var migratedCount = await connection.ExecuteAsync(migrationSql);
        if (migratedCount > 0)
        {
            logger.LogInformation("Migrated {Count} existing chats to 'funny' mode", migratedCount);
        }
    }

    private async Task CreateUserAliasesTableAsync(System.Data.IDbConnection connection)
    {
        // Table to store all known names/aliases for each user in each chat
        // This enables resolving "Бексултан" → user_id even if user changed name to "Beksultan Valiev"
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS user_aliases (
                id SERIAL PRIMARY KEY,
                chat_id BIGINT NOT NULL,
                user_id BIGINT NOT NULL,
                alias VARCHAR(255) NOT NULL,
                alias_type VARCHAR(20) NOT NULL DEFAULT 'display_name',
                usage_count INT NOT NULL DEFAULT 1,
                first_seen TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                last_seen TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                UNIQUE(chat_id, user_id, alias)
            );

            CREATE INDEX IF NOT EXISTS idx_user_aliases_chat_alias
            ON user_aliases (chat_id, LOWER(alias));

            CREATE INDEX IF NOT EXISTS idx_user_aliases_chat_user
            ON user_aliases (chat_id, user_id);
            """;

        await connection.ExecuteAsync(createTableSql);

        // Migration: populate from existing messages
        const string migrationSql = """
            INSERT INTO user_aliases (chat_id, user_id, alias, alias_type, usage_count, first_seen, last_seen)
            SELECT
                chat_id,
                from_user_id,
                display_name,
                'display_name',
                COUNT(*),
                MIN(date_utc),
                MAX(date_utc)
            FROM messages
            WHERE display_name IS NOT NULL
              AND display_name != ''
              AND from_user_id > 0
            GROUP BY chat_id, from_user_id, display_name
            ON CONFLICT (chat_id, user_id, alias) DO UPDATE SET
                usage_count = user_aliases.usage_count + EXCLUDED.usage_count,
                last_seen = GREATEST(user_aliases.last_seen, EXCLUDED.last_seen);
            """;

        var migratedCount = await connection.ExecuteAsync(migrationSql);
        if (migratedCount > 0)
        {
            logger.LogInformation("Migrated {Count} user aliases from messages", migratedCount);
        }

        // Also add usernames as aliases
        const string usernameMigrationSql = """
            INSERT INTO user_aliases (chat_id, user_id, alias, alias_type, usage_count, first_seen, last_seen)
            SELECT
                chat_id,
                from_user_id,
                username,
                'username',
                COUNT(*),
                MIN(date_utc),
                MAX(date_utc)
            FROM messages
            WHERE username IS NOT NULL
              AND username != ''
              AND from_user_id > 0
            GROUP BY chat_id, from_user_id, username
            ON CONFLICT (chat_id, user_id, alias) DO UPDATE SET
                usage_count = user_aliases.usage_count + EXCLUDED.usage_count,
                last_seen = GREATEST(user_aliases.last_seen, EXCLUDED.last_seen);
            """;

        var usernameCount = await connection.ExecuteAsync(usernameMigrationSql);
        if (usernameCount > 0)
        {
            logger.LogInformation("Migrated {Count} username aliases from messages", usernameCount);
        }
    }

    private static async Task CreateUserRelationshipsTableAsync(System.Data.IDbConnection connection)
    {
        // Table to store relationships between chat participants
        // E.g., "Глеб's wife is Маша", "Петя is Васи's brother"
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS user_relationships (
                id SERIAL PRIMARY KEY,
                chat_id BIGINT NOT NULL,
                user_id BIGINT NOT NULL,
                related_user_id BIGINT,
                related_person_name VARCHAR(255) NOT NULL,
                relationship_type VARCHAR(30) NOT NULL,
                relationship_label VARCHAR(50),
                confidence REAL DEFAULT 0.7,
                mention_count INT DEFAULT 1,
                source_message_ids BIGINT[],
                is_active BOOLEAN DEFAULT TRUE,
                first_seen TIMESTAMPTZ DEFAULT NOW(),
                last_seen TIMESTAMPTZ DEFAULT NOW(),
                ended_at TIMESTAMPTZ,
                end_reason TEXT,
                UNIQUE(chat_id, user_id, related_person_name, relationship_type)
            );

            CREATE INDEX IF NOT EXISTS idx_relationships_lookup
            ON user_relationships (chat_id, user_id) WHERE is_active = TRUE;

            CREATE INDEX IF NOT EXISTS idx_relationships_person
            ON user_relationships (chat_id, LOWER(related_person_name)) WHERE is_active = TRUE;

            CREATE INDEX IF NOT EXISTS idx_relationships_related_user
            ON user_relationships (chat_id, related_user_id) WHERE related_user_id IS NOT NULL AND is_active = TRUE;
            """;

        await connection.ExecuteAsync(createTableSql);
    }

    private async Task CreateIndexesAsync(System.Data.IDbConnection connection)
    {
        // Messages indexes
        var messagesIndexes = new[]
        {
            "CREATE INDEX IF NOT EXISTS idx_messages_chat_id ON messages (chat_id);",
            "CREATE INDEX IF NOT EXISTS idx_messages_date_utc ON messages (date_utc);",
            "CREATE INDEX IF NOT EXISTS idx_messages_from_user_id ON messages (from_user_id);",
            "CREATE INDEX IF NOT EXISTS idx_messages_chat_date ON messages (chat_id, date_utc DESC);",
            // Composite index for user-specific queries (PersonalSearchService)
            "CREATE INDEX IF NOT EXISTS idx_messages_user_date ON messages (chat_id, from_user_id, date_utc DESC);"
        };

        foreach (var indexSql in messagesIndexes)
        {
            await connection.ExecuteAsync(indexSql);
        }

        // Embeddings indexes
        try
        {
            var embeddingsIndexes = new[]
            {
                // Index for filtering by chat
                "CREATE INDEX IF NOT EXISTS idx_embeddings_chat_id ON message_embeddings (chat_id);",

                // Index for looking up by message
                "CREATE INDEX IF NOT EXISTS idx_embeddings_chat_message ON message_embeddings (chat_id, message_id);",

                // GIN index for metadata JSONB queries (speeds up PersonalSearchService)
                "CREATE INDEX IF NOT EXISTS idx_message_embeddings_metadata_gin ON message_embeddings USING GIN (metadata jsonb_path_ops);",

                // Full-text search index for Russian text
                "CREATE INDEX IF NOT EXISTS idx_message_embeddings_text_search ON message_embeddings USING GIN (to_tsvector('russian', chunk_text));",

                // Vector similarity search index (HNSW for fast approximate nearest neighbors)
                // HNSW provides O(log n) search time, optimal for large datasets (100K+ vectors)
                """
                CREATE INDEX IF NOT EXISTS idx_embeddings_vector
                ON message_embeddings
                USING hnsw (embedding vector_cosine_ops);
                """
            };

            foreach (var indexSql in embeddingsIndexes)
            {
                await connection.ExecuteAsync(indexSql);
            }

            logger.LogInformation("Embeddings indexes created");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create embeddings indexes. pgvector may not be installed");
        }

        // Context embeddings indexes
        try
        {
            var contextEmbeddingsIndexes = new[]
            {
                "CREATE INDEX IF NOT EXISTS idx_context_embeddings_chat_id ON context_embeddings (chat_id);",
                "CREATE INDEX IF NOT EXISTS idx_context_embeddings_center ON context_embeddings (chat_id, center_message_id);",
                "CREATE INDEX IF NOT EXISTS idx_context_embeddings_range ON context_embeddings (chat_id, window_start_id, window_end_id);",

                // Full-text search index for Russian text (for hybrid BM25 + vector search)
                "CREATE INDEX IF NOT EXISTS idx_context_embeddings_text_search ON context_embeddings USING GIN (to_tsvector('russian', context_text));",

                // Vector similarity search index (HNSW for fast approximate nearest neighbors)
                """
                CREATE INDEX IF NOT EXISTS idx_context_embeddings_vector
                ON context_embeddings
                USING hnsw (embedding vector_cosine_ops);
                """
            };

            foreach (var indexSql in contextEmbeddingsIndexes)
            {
                await connection.ExecuteAsync(indexSql);
            }

            logger.LogInformation("Context embeddings indexes created");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create context embeddings indexes");
        }
    }
}
