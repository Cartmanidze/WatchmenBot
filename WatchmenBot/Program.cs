using Telegram.Bot;
using WatchmenBot.Services;

var builder = WebApplication.CreateBuilder(args);

var configuration = builder.Configuration;
var services = builder.Services;

services.AddSingleton(sp =>
{
    var token = configuration["Telegram:BotToken"] ?? string.Empty;
    return new TelegramBotClient(token);
});

services.AddSingleton(sp =>
{
    var dbPath = configuration["Storage:LiteDbPath"] ?? "Data/bot.db";
    return new MessageStore(dbPath);
});

services.AddHttpClient<KimiClient>()
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var useProxy = bool.TryParse(configuration["Kimi:UseProxy"], out var v) ? v : true;
        return new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            UseProxy = useProxy
        };
    })
    .AddTypedClient((http, sp) =>
    {
        var apiKey = configuration["Kimi:ApiKey"] ?? string.Empty;
        var baseUrl = configuration["Kimi:BaseUrl"] ?? "https://openrouter.ai/api";
        var model = configuration["Kimi:Model"] ?? "moonshotai/kimi-k2";
        return new KimiClient(http, apiKey, baseUrl, model);
    });

services.AddHostedService<TelegramBotRunner>();
services.AddHostedService<DailySummaryService>();

var app = builder.Build();

app.MapGet("/", () => "WatchmenBot is running");

app.Run();