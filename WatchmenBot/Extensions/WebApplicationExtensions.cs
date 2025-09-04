namespace WatchmenBot.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication ConfigureWatchmenBot(this WebApplication app)
    {
        // Health check endpoints
        app.MapGet("/", () => new { 
            service = "WatchmenBot", 
            status = "running",
            timestamp = DateTimeOffset.UtcNow 
        });
        
        app.MapHealthChecks("/health");

        // Map controllers for webhook and admin endpoints
        app.MapControllers();

        return app;
    }
}