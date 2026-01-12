using Dapper;
using WatchmenBot.Extensions;

// Enable Dapper snake_case → PascalCase mapping globally
// Required for RETURNING * to work with C# models
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

// Configure services
builder.Services.AddWatchmenBotServices(builder.Configuration);

var app = builder.Build();

// Configure application
app.ConfigureWatchmenBot();

app.Run();