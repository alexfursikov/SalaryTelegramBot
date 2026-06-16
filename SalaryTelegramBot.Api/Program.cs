using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalaryTelegramBot.Api.Configuration;
using SalaryTelegramBot.Api.Data;
using SalaryTelegramBot.Api.Services;
using Telegram.Bot;

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
        SslMode = SslMode.Require,
        Pooling = true,
        MinPoolSize = 1,
        MaxPoolSize = 5,
        ConnectionIdleLifetime = 60,
        ConnectionPruningInterval = 10,
        CommandTimeout = 60,
        Timeout = 30
    };
    connectionString = pgBuilder.ConnectionString;
}

builder.Services.AddDbContext<AppDbContext>(x =>
    x.UseNpgsql(connectionString, o =>
    {
        o.CommandTimeout(60);
        o.EnableRetryOnFailure(3);
    }));

builder.Services.AddControllers();
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddSingleton<BotStateService>();
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddHttpClient<NbrbRateService>();
builder.Services.AddScoped<SalaryService>();
builder.Services.AddScoped<SalaryScheduleService>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddHostedService<SchedulerService>();

var botTokenForDi = builder.Configuration["Telegram:Token"]
    ?? Environment.GetEnvironmentVariable("TELEGRAM__TOKEN")
    ?? Environment.GetEnvironmentVariable("TELEGRAM__Token")
    ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
if (!string.IsNullOrWhiteSpace(botTokenForDi))
    builder.Services.AddSingleton(new Telegram.Bot.TelegramBotClient(botTokenForDi));

var app = builder.Build();

try
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.SetCommandTimeout(60);
        
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"BotSettings\" ADD COLUMN IF NOT EXISTS \"Currency\" text NOT NULL DEFAULT 'RUB'");
        
        db.Database.Migrate();
        await SeedData.SeedAsync(db);
        Console.WriteLine("Database ready.");
    }

    var botToken = builder.Configuration["Telegram:Token"]
        ?? Environment.GetEnvironmentVariable("TELEGRAM__TOKEN")
        ?? Environment.GetEnvironmentVariable("TELEGRAM__Token")
        ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");

    if (!string.IsNullOrWhiteSpace(botToken))
    {
        var bot = new TelegramBotClient(botToken);
        var baseUrl = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL")
            ?? "https://salarytelegrambot.onrender.com";
        var webhookUrl = $"{baseUrl}/webhook";

        Console.WriteLine($"Setting webhook to {webhookUrl}");
        await bot.SetWebhook(
            webhookUrl,
            allowedUpdates: [],
            dropPendingUpdates: true);
        Console.WriteLine("Webhook set successfully.");
    }

    app.MapControllers();
    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FATAL: {ex}");
    throw;
}
