using System.Data;

namespace WatchmenBot.Infrastructure.Database;

public interface IDbConnectionFactory
{
    Task<IDbConnection> CreateConnectionAsync();
    IDbConnection CreateConnection();
}