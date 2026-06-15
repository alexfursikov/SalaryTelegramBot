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
    var withoutScheme = connectionString["postgresql://".Length..];
    if (connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        withoutScheme = connectionString["postgres://".Length..];

    var atIndex = withoutScheme.LastIndexOf('@');
    if (atIndex < 0)
        throw new InvalidOperationException("Invalid connection URI: no '@' found.");

    var userPart = withoutScheme[..atIndex];
    var hostPart = withoutScheme[(atIndex + 1)..];

    var colonIndex = userPart.IndexOf(':');
    var username = colonIndex >= 0 ? Uri.UnescapeDataString(userPart[..colonIndex]) : Uri.UnescapeDataString(userPart);
    var password = colonIndex >= 0 ? Uri.UnescapeDataString(userPart[(colonIndex + 1)..]) : "";

    var slashIndex = hostPart.IndexOf('/');
    var hostPort = slashIndex >= 0 ? hostPart[..slashIndex] : hostPart;
    var database = slashIndex >= 0 ? hostPart[(slashIndex + 1)..] : "postgres";

    var portColonIndex = hostPort.IndexOf(':');
    var host = portColonIndex >= 0 ? hostPort[..portColonIndex] : hostPort;
    var port = portColonIndex >= 0 && int.TryParse(hostPort[(portColonIndex + 1)..], out var p) ? p : 5432;

    var pgBuilder = new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = port,
        Database = database,
        Username = username,
        Password = password,
        SslMode = SslMode.Require
    };
    connectionString = pgBuilder.ConnectionString;
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
