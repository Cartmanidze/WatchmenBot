using Hangfire;
using Hangfire.PostgreSql;
using WatchmenBot.Features.Webhook.Jobs;

namespace WatchmenBot.Infrastructure.Hangfire;

/// <summary>
/// Hangfire configuration extensions for WatchmenBot.
/// Replaces custom BackgroundWorkers with Hangfire's job processing infrastructure.
/// </summary>
public static class HangfireExtensions
{
    /// <summary>
    /// Add Hangfire services with PostgreSQL storage.
    /// </summary>
    public static IServiceCollection AddHangfireServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default") ??
                               configuration["Database:ConnectionString"] ??
                               throw new InvalidOperationException("Database connection string is required for Hangfire");

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options =>
            {
                options.UseNpgsqlConnection(connectionString);
            }, new PostgreSqlStorageOptions
            {
                SchemaName = "hangfire",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.FromSeconds(2), // Fast polling for instant response
                InvisibilityTimeout = TimeSpan.FromMinutes(30), // Long timeout for LLM operations
                DistributedLockTimeout = TimeSpan.FromMinutes(10)
            }));

        // Add Hangfire server with configured queues
        services.AddHangfireServer(options =>
        {
            options.WorkerCount = Environment.ProcessorCount * 2;
            options.Queues = ["critical", "default", "low"];
            options.ServerName = $"watchmenbot-{Environment.MachineName}";
        });

        return services;
    }

    /// <summary>
    /// Configure Hangfire dashboard and recurring jobs.
    /// </summary>
    public static WebApplication UseHangfireDashboard(this WebApplication app, IConfiguration configuration)
    {
        // Dashboard with basic auth (configure in appsettings)
        var dashboardPath = configuration["Hangfire:DashboardPath"] ?? "/hangfire";
        var dashboardUser = configuration["Hangfire:DashboardUser"];
        var dashboardPassword = configuration["Hangfire:DashboardPassword"];

        var dashboardOptions = new DashboardOptions
        {
            DashboardTitle = "WatchmenBot Jobs",
            DisplayStorageConnectionString = false,
            StatsPollingInterval = 5000 // 5 seconds
        };

        // Add basic auth if configured
        if (!string.IsNullOrEmpty(dashboardUser) && !string.IsNullOrEmpty(dashboardPassword))
        {
            dashboardOptions.Authorization =
            [
                new HangfireBasicAuthFilter(dashboardUser, dashboardPassword)
            ];
        }

        app.UseHangfireDashboard(dashboardPath, dashboardOptions);

        // Register recurring jobs
        RegisterRecurringJobs();

        return app;
    }

    /// <summary>
    /// Register all Hangfire recurring jobs.
    /// </summary>
    private static void RegisterRecurringJobs()
    {
        // Webhook Watchdog: Check every 5 minutes, auto-recover if webhook is broken
        RecurringJob.AddOrUpdate<WebhookWatchdogJob>(
            recurringJobId: "webhook-watchdog",
            queue: "critical", // Fast execution priority
            methodCall: job => job.ExecuteAsync(),
            cronExpression: "*/5 * * * *", // Every 5 minutes
            options: new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
                MisfireHandling = MisfireHandlingMode.Ignorable // Skip if missed, will run next interval
            });
    }
}