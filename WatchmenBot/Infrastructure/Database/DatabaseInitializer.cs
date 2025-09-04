using Dapper;

namespace WatchmenBot.Infrastructure.Database;

public class DatabaseInitializer : IHostedService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IDbConnectionFactory connectionFactory, ILogger<DatabaseInitializer> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            await CreateTablesAsync(connection);
            await CreateIndexesAsync(connection);
            _logger.LogInformation("Database initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task CreateTablesAsync(System.Data.IDbConnection connection)
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

    private static async Task CreateIndexesAsync(System.Data.IDbConnection connection)
    {
        var indexes = new[]
        {
            "CREATE INDEX IF NOT EXISTS idx_messages_chat_id ON messages (chat_id);",
            "CREATE INDEX IF NOT EXISTS idx_messages_date_utc ON messages (date_utc);",
            "CREATE INDEX IF NOT EXISTS idx_messages_from_user_id ON messages (from_user_id);",
            "CREATE INDEX IF NOT EXISTS idx_messages_chat_date ON messages (chat_id, date_utc);"
        };

        foreach (var indexSql in indexes)
        {
            await connection.ExecuteAsync(indexSql);
        }
    }
}