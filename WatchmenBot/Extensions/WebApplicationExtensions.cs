namespace WatchmenBot.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication ConfigureWatchmenBot(this WebApplication app)
    {
        // Health check endpoint
        app.MapGet("/", () => new { 
            service = "WatchmenBot", 
            status = "running",
            timestamp = DateTimeOffset.UtcNow 
        });

        // Map controllers for webhook and admin endpoints
        app.MapControllers();

        return app;
    }
}