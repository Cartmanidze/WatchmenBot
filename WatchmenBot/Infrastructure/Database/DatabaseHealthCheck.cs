using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WatchmenBot.Infrastructure.Database;

public class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbConnectionFactory _connectionFactory;

    public DatabaseHealthCheck(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var result = await connection.QuerySingleAsync<int>("SELECT 1;");
            
            return result == 1 
                ? HealthCheckResult.Healthy("PostgreSQL connection is healthy") 
                : HealthCheckResult.Unhealthy("PostgreSQL returned unexpected result");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connection failed", ex);
        }
    }
}