using System.Security.Authentication;
using Microsoft.OpenApi.Models;
using Telegram.Bot;
using WatchmenBot.Features.Admin;
using WatchmenBot.Features.Messages;
using WatchmenBot.Features.Webhook;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Services;

namespace WatchmenBot.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWatchmenBotServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Telegram Bot Client
        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var token = configuration["Telegram:BotToken"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Telegram:BotToken is required");
            }
            return new TelegramBotClient(token);
        });

        // Database
        services.AddSingleton<IDbConnectionFactory>(sp =>
        {
            var connectionString = configuration.GetConnectionString("Default") ?? 
                                 configuration["Database:ConnectionString"] ?? 
                                 throw new InvalidOperationException("Database connection string is required");
            return new PostgreSqlConnectionFactory(connectionString);
        });

        services.AddScoped<MessageStore>();
        services.AddHostedService<DatabaseInitializer>();
        
        // Health checks
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("postgresql");

        // Kimi Client with HttpClient
        services.AddHttpClient<KimiClient>()
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var useProxy = configuration.GetValue<bool>("Kimi:UseProxy", true);
                return new HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | 
                                           System.Net.DecompressionMethods.Deflate,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    UseProxy = useProxy
                };
            })
            .AddTypedClient<KimiClient>((httpClient, serviceProvider) =>
            {
                var apiKey = configuration["Kimi:ApiKey"] ?? string.Empty;
                var baseUrl = configuration["Kimi:BaseUrl"] ?? "https://openrouter.ai/api";
                var model = configuration["Kimi:Model"] ?? "moonshotai/kimi-k2";
                
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException("Kimi:ApiKey is required");
                }
                
                return new KimiClient(httpClient, apiKey, baseUrl, model);
            });

        // Background Services
        services.AddHostedService<DailySummaryService>();

        // Feature Handlers
        services.AddScoped<ProcessTelegramUpdateHandler>();
        services.AddScoped<SaveMessageHandler>();
        services.AddScoped<SetWebhookHandler>();
        services.AddScoped<DeleteWebhookHandler>();
        services.AddScoped<GetWebhookInfoHandler>();

        // Controllers
        services.AddControllers();

        // Swagger / OpenAPI
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "WatchmenBot API",
                Version = "v1"
            });
        });

        return services;
    }
}