using Microsoft.EntityFrameworkCore;
using SalaryTelegramBot.Api.Configuration;
using SalaryTelegramBot.Api.Data;
using SalaryTelegramBot.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SalarySettings>(
    builder.Configuration.GetSection("Salary"));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
    connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

Console.WriteLine($"Connection string loaded: length={connectionString?.Length ?? -1}");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Database connection string not configured. " +
        "Set ConnectionStrings__DefaultConnection env var in Render.");
}

if (!connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) &&
    !connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
    !connectionString.Contains("SSL Mode", StringComparison.OrdinalIgnoreCase) &&
    !connectionString.Contains("sslmode", StringComparison.OrdinalIgnoreCase))
{
    connectionString += connectionString.Contains(';') ? ";SSL Mode=Require" : "?SSL Mode=Require";
}

builder.Services.AddDbContext<AppDbContext>(x => x.UseNpgsql(connectionString));

builder.Services.AddScoped<SalaryService>();
builder.Services.AddScoped<SalaryScheduleService>();

builder.Services.AddSingleton<BotStateService>();
builder.Services.AddSingleton<RateLimiter>();

builder.Services.AddHostedService<TelegramBotService>();
builder.Services.AddHostedService<SchedulerService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await SeedData.SeedAsync(db);
}

app.Run();
