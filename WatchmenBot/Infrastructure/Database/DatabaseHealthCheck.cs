using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WatchmenBot.Infrastructure.Database;

public class DatabaseHealthCheck(IDbConnectionFactory connectionFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();
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