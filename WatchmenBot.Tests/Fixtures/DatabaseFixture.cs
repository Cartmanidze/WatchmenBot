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

        // Create messages table
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS messages (
                message_id BIGINT PRIMARY KEY,
                chat_id BIGINT NOT NULL,
                from_user_id BIGINT,
                text TEXT,
                date_utc TIMESTAMPTZ NOT NULL,
                created_at TIMESTAMPTZ DEFAULT NOW()
            )
            """);

        // Create message_embeddings table
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS message_embeddings (
                id BIGSERIAL PRIMARY KEY,
                chat_id BIGINT NOT NULL,
                message_id BIGINT NOT NULL,
                chunk_index INT NOT NULL,
                chunk_text TEXT NOT NULL,
                embedding vector(1024),
                metadata JSONB,
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
                context_text TEXT NOT NULL,
                embedding vector(1024),
                created_at TIMESTAMPTZ DEFAULT NOW()
            )
            """);

        // Create user_profiles table
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS user_profiles (
                user_id BIGINT NOT NULL,
                chat_id BIGINT NOT NULL,
                display_name TEXT,
                username TEXT,
                facts JSONB,
                traits JSONB,
                interests JSONB,
                notable_quotes JSONB,
                interaction_count INT DEFAULT 0,
                last_interaction TIMESTAMPTZ,
                message_count INT DEFAULT 0,
                summary TEXT,
                communication_style TEXT,
                role_in_chat TEXT,
                roast_material JSONB,
                updated_at TIMESTAMPTZ DEFAULT NOW(),
                PRIMARY KEY (chat_id, user_id)
            )
            """);

        // Create user_aliases table
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS user_aliases (
                id BIGSERIAL PRIMARY KEY,
                chat_id BIGINT NOT NULL,
                user_id BIGINT NOT NULL,
                alias TEXT NOT NULL,
                alias_type TEXT NOT NULL,
                usage_count INT DEFAULT 1,
                last_used TIMESTAMPTZ DEFAULT NOW(),
                created_at TIMESTAMPTZ DEFAULT NOW(),
                UNIQUE(chat_id, alias)
            )
            """);

        // Create user_facts table
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS user_facts (
                id BIGSERIAL PRIMARY KEY,
                user_id BIGINT NOT NULL,
                chat_id BIGINT NOT NULL,
                fact_type TEXT NOT NULL,
                fact_text TEXT NOT NULL,
                confidence DOUBLE PRECISION NOT NULL,
                created_at TIMESTAMPTZ DEFAULT NOW()
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
                topics JSONB,
                extracted_facts JSONB,
                created_at TIMESTAMPTZ DEFAULT NOW()
            )
            """);

        // Create indexes
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_messages_chat ON messages(chat_id, date_utc DESC)");
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_message_embeddings_chat ON message_embeddings(chat_id)");
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_context_embeddings_chat ON context_embeddings(chat_id)");
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
