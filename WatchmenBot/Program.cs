using Dapper;
using Serilog;
using Serilog.Events;
using WatchmenBot.Extensions;

// Enable Dapper snake_case → PascalCase mapping globally
// Required for RETURNING * to work with C# models
DefaultTypeMap.MatchNamesWithUnderscores = true;

// Configure Serilog for structured logging with Seq
var seqUrl = Environment.GetEnvironmentVariable("SEQ_URL") ?? "http://localhost:5341";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "WatchmenBot")
    .WriteTo.Console(
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.Seq(seqUrl)
    .CreateLogger();

try
{
    Log.Information("Starting WatchmenBot...");

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog for all logging
    builder.Host.UseSerilog();

    // Configure services
    builder.Services.AddWatchmenBotServices(builder.Configuration);

    var app = builder.Build();

    // Configure application
    app.ConfigureWatchmenBot();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}