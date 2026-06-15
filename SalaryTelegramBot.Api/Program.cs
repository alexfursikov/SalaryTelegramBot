using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalaryTelegramBot.Api.Configuration;
using SalaryTelegramBot.Api.Data;
using SalaryTelegramBot.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SalarySettings>(
    builder.Configuration.GetSection("Salary"));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
    connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Database connection string not configured. " +
        "Set ConnectionStrings__DefaultConnection env var in Render.");
}

connectionString = connectionString.Trim().Trim('"');

if (connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
    connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
{
    var match = Regex.Match(connectionString,
        @"^postgre(s)?://([^:]+):([^@]+)@([^:/]+)(?::(\d+))?/([^?]+)");

    if (!match.Success)
        throw new InvalidOperationException($"Cannot parse connection URI: {connectionString[..Math.Min(30, connectionString.Length)]}...");

    var sb = new NpgsqlConnectionStringBuilder
    {
        Host = match.Groups[4].Value,
        Port = match.Groups[5].Success ? int.Parse(match.Groups[5].Value) : 5432,
        Database = match.Groups[6].Value,
        Username = Uri.UnescapeDataString(match.Groups[2].Value),
        Password = Uri.UnescapeDataString(match.Groups[3].Value),
        SslMode = SslMode.Require
    };
    connectionString = sb.ConnectionString;
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
