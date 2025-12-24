using Dapper;

namespace WatchmenBot.Infrastructure.Database;

public class DatabaseInitializer : IHostedService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly IConfiguration _configuration;

    public DatabaseInitializer(
        IDbConnectionFactory connectionFactory,
        ILogger<DatabaseInitializer> logger,
        IConfiguration configuration)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

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

            // Create indexes
            await CreateIndexesAsync(connection);

            _logger.LogInformation("Database initialized successfully with pgvector support");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnablePgVectorAsync(System.Data.IDbConnection connection)
    {
        try
        {
            await connection.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector;");
            _logger.LogInformation("pgvector extension enabled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enable pgvector extension. RAG features will be disabled. " +
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
        var dimensions = _configuration.GetValue<int>("Embeddings:Dimensions", 1536);

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

        try
        {
            await connection.ExecuteAsync(createTableSql);
            _logger.LogInformation("Embeddings table created with {Dimensions} dimensions", dimensions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create embeddings table. pgvector may not be installed");
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

    private async Task CreateIndexesAsync(System.Data.IDbConnection connection)
    {
        // Messages indexes
        var messagesIndexes = new[]
        {
            "CREATE INDEX IF NOT EXISTS idx_messages_chat_id ON messages (chat_id);",
            "CREATE INDEX IF NOT EXISTS idx_messages_date_utc ON messages (date_utc);",
            "CREATE INDEX IF NOT EXISTS idx_messages_from_user_id ON messages (from_user_id);",
            "CREATE INDEX IF NOT EXISTS idx_messages_chat_date ON messages (chat_id, date_utc);"
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

                // Vector similarity search index (IVFFlat for approximate nearest neighbors)
                // lists = sqrt(n) where n is expected number of rows, 100 is good for up to 10K rows
                """
                CREATE INDEX IF NOT EXISTS idx_embeddings_vector
                ON message_embeddings
                USING ivfflat (embedding vector_cosine_ops)
                WITH (lists = 100);
                """
            };

            foreach (var indexSql in embeddingsIndexes)
            {
                await connection.ExecuteAsync(indexSql);
            }

            _logger.LogInformation("Embeddings indexes created");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create embeddings indexes. pgvector may not be installed");
        }
    }
}
