using WatchmenBot.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Configure services
builder.Services.AddWatchmenBotServices(builder.Configuration);

var app = builder.Build();

// Configure application
app.ConfigureWatchmenBot();

app.Run();