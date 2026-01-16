using Dapper;
using Testcontainers.PostgreSql;
using WatchmenBot.Infrastructure.Database;
using Xunit;

namespace WatchmenBot.Tests.Fixtures;

/// <summary>
/// Fixture for spinning up PostgreSQL test database using Testcontainers
/// </summary>
public class DatabaseFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public IDbConnectionFactory? ConnectionFactory { get; private set; }
    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg17")
            .WithDatabase("watchmenbot_test")
            .WithUsername("postgres")
            .WithPassword("test_password")
            .Build();

        await _container.StartAsync();

        ConnectionString = _container.GetConnectionString();
        ConnectionFactory = new PostgreSqlConnectionFactory(ConnectionString);

        // Initialize database schema
        await InitializeDatabaseSchemaAsync();
    }

    private async Task InitializeDatabaseSchemaAsync()
    {
        if (ConnectionFactory == null)
            throw new InvalidOperationException("ConnectionFactory not initialized");

        using var connection = await ConnectionFactory.CreateConnectionAsync();

        // Create pgvector extension
        await connection.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");

        // Create chats table (required by messages foreign key and MessageStore)
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS chats (
                id BIGINT PRIMARY KEY,
                title VARCHAR(255) NULL,
                type VARCHAR(50) NOT NULL DEFAULT 'group',
                updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
            )
            """);

        // Create messages table (matching production schema)
        await connection.ExecuteAsync("""
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
                is_forwarded BOOLEAN DEFAULT FALSE,
                forward_origin_type VARCHAR(20),
                forward_from_name VARCHAR(255),
                forward_from_id BIGINT,
                forward_date TIMESTAMPTZ,
                created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                PRIMARY KEY (chat_id, id)
            )
            """);

        // Create message_embeddings table
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS message_embeddings (
                id BIGSERIAL PRIMARY KEY,
                chat_id BIGINT NOT NULL,
                message_id BIGINT NOT NULL,
                chunk_index INT NOT NULL DEFAULT 0,
                chunk_text TEXT NOT NULL,
                embedding vector(1024),
                metadata JSONB,
                is_question BOOLEAN DEFAULT FALSE,
                source_message_id BIGINT,
                created_at TIMESTAMPTZ DEFAULT NOW(),
                UNIQUE(chat_id, message_id, chunk_index)
            )
            """);

        // Create context_embeddings table
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS context_embeddings (
                id BIGSERIAL PRIMARY KEY,
                chat_id BIGINT NOT NULL,
                center_message_id BIGINT NOT NULL,
                window_start_id BIGINT NOT NULL,
                window_end_id BIGINT NOT NULL,
                message_ids BIGINT[] NOT NULL,
                context_text TEXT NOT NULL,
                embedding vector(1024),
                window_size INT NOT NULL DEFAULT 10,
                created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                UNIQUE(chat_id, center_message_id)
            )
            """);

        // Create user_profiles table
        await connection.ExecuteAsync("""
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
                message_count INT DEFAULT 0,
                last_message_at TIMESTAMPTZ,
                active_hours JSONB DEFAULT '{}',
                summary TEXT,
                communication_style TEXT,
                role_in_chat TEXT,
                roast_material JSONB DEFAULT '[]',
                profile_version INT DEFAULT 1,
                last_profile_update TIMESTAMPTZ,
                gender VARCHAR(10),
                gender_confidence FLOAT DEFAULT 0,
                created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                PRIMARY KEY (chat_id, user_id)
            )
            """);

        // Create user_aliases table (matching production schema)
        await connection.ExecuteAsync("""
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
            )
            """);

        // Create user_facts table
        await connection.ExecuteAsync("""
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
            )
            """);

        // Create conversation_memory table
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS conversation_memory (
                id BIGSERIAL PRIMARY KEY,
                user_id BIGINT NOT NULL,
                chat_id BIGINT NOT NULL,
                query TEXT NOT NULL,
                response_summary TEXT NOT NULL,
                topics JSONB NOT NULL DEFAULT '[]'::jsonb,
                extracted_facts JSONB NOT NULL DEFAULT '[]'::jsonb,
                created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
            )
            """);

        // Create user_relationships table
        await connection.ExecuteAsync("""
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
            )
            """);

        // Create message_queue table (for profile analysis)
        await connection.ExecuteAsync("""
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
            )
            """);

        // Create indexes (matching production)
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_messages_chat_id ON messages (chat_id)");
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_messages_date_utc ON messages (date_utc)");
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_messages_from_user_id ON messages (from_user_id)");
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_messages_chat_date ON messages (chat_id, date_utc DESC)");
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_embeddings_chat_id ON message_embeddings (chat_id)");
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_embeddings_chat_message ON message_embeddings (chat_id, message_id)");
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_context_embeddings_chat_id ON context_embeddings (chat_id)");
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_user_aliases_chat_alias ON user_aliases (chat_id, LOWER(alias))");
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_user_aliases_chat_user ON user_aliases (chat_id, user_id)");
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_user_facts_lookup ON user_facts (chat_id, user_id)");
    }

    /// <summary>
    /// Clean all test data from database.
    /// Call this between tests for isolation.
    /// </summary>
    public async Task CleanupAsync()
    {
        if (ConnectionFactory == null)
            throw new InvalidOperationException("ConnectionFactory not initialized");

        using var connection = await ConnectionFactory.CreateConnectionAsync();
        await connection.ExecuteAsync(@"
            TRUNCATE TABLE
                conversation_memory,
                user_relationships,
                user_facts,
                user_aliases,
                user_profiles,
                context_embeddings,
                message_embeddings,
                message_queue,
                messages,
                chats
            CASCADE
        ");
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition("Database")]
public class DatabaseTestCollection : ICollectionFixture<DatabaseFixture>;
