namespace WatchmenBot.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication ConfigureWatchmenBot(this WebApplication app)
    {
        // Swagger middleware & UI
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "WatchmenBot API v1");
        });
        
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