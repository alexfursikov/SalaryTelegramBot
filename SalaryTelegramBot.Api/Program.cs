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
builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<UserKeyCache>();
builder.Services.AddHttpClient<NbrbRateService>();
builder.Services.AddScoped<SalaryService>();
builder.Services.AddScoped<SalaryScheduleService>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddHostedService<TelegramPollingService>();

var botTokenForDi = builder.Configuration["Telegram:Token"]
    ?? Environment.GetEnvironmentVariable("TELEGRAM__TOKEN")
    ?? Environment.GetEnvironmentVariable("TELEGRAM__Token")
    ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
if (!string.IsNullOrWhiteSpace(botTokenForDi))
    builder.Services.AddSingleton(new Telegram.Bot.TelegramBotClient(botTokenForDi));

var app = builder.Build();

try
{
    for (int i = 0; i < 3; i++)
    {
        try
        {
            using var dbScope = app.Services.CreateScope();
            var db = dbScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.SetCommandTimeout(120);

            await db.Database.ExecuteSqlRawAsync(@"
                DO $$ BEGIN
                    CREATE TABLE IF NOT EXISTS ""AccrualRules"" (
                        ""Id"" serial PRIMARY KEY, ""ChatId"" bigint NOT NULL,
                        ""DayOfMonth"" integer NOT NULL, ""Amount"" numeric NOT NULL);
                    CREATE TABLE IF NOT EXISTS ""BotSettings"" (
                        ""Id"" serial PRIMARY KEY, ""ChatId"" bigint NOT NULL,
                        ""CheckHour"" integer NOT NULL DEFAULT 12, ""CheckMinute"" integer NOT NULL DEFAULT 0,
                        ""IsNdflEnabled"" boolean NOT NULL DEFAULT true,
                        ""NdflStartDay"" integer, ""NdflStartMonth"" integer, ""NdflStartYear"" integer,
                        ""CalculationStartMonth"" integer, ""CalculationStartYear"" integer, ""CalculationStartDay"" integer,
                        ""Currency"" text NOT NULL DEFAULT 'RUB');
                    CREATE TABLE IF NOT EXISTS ""Transactions"" (
                        ""Id"" serial PRIMARY KEY, ""ChatId"" bigint NOT NULL,
                        ""Date"" timestamp with time zone NOT NULL, ""Amount"" numeric NOT NULL,
                        ""Type"" integer NOT NULL, ""Comment"" text NOT NULL DEFAULT '');
                    ALTER TABLE ""BotSettings"" ADD COLUMN IF NOT EXISTS ""Currency"" text NOT NULL DEFAULT 'RUB';
                    ALTER TABLE ""BotSettings"" ADD COLUMN IF NOT EXISTS ""PasswordSalt"" bytea;
                    ALTER TABLE ""BotSettings"" ADD COLUMN IF NOT EXISTS ""PasswordProof"" bytea;
                    ALTER TABLE ""BotSettings"" ADD COLUMN IF NOT EXISTS ""IsEncrypted"" boolean NOT NULL DEFAULT false;
                    ALTER TABLE ""Transactions"" ADD COLUMN IF NOT EXISTS ""EncryptedAmount"" bytea;
                    ALTER TABLE ""AccrualRules"" ADD COLUMN IF NOT EXISTS ""EncryptedAmount"" bytea;
                    ALTER TABLE ""Transactions"" ALTER COLUMN ""Amount"" DROP NOT NULL;
                    ALTER TABLE ""Transactions"" ALTER COLUMN ""Amount"" SET DEFAULT 0;
                    ALTER TABLE ""AccrualRules"" ALTER COLUMN ""Amount"" DROP NOT NULL;
                    ALTER TABLE ""AccrualRules"" ALTER COLUMN ""Amount"" SET DEFAULT 0;
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_AccrualRules_ChatId_DayOfMonth"" ON ""AccrualRules"" (""ChatId"", ""DayOfMonth"");
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_BotSettings_ChatId"" ON ""BotSettings"" (""ChatId"");
                    CREATE INDEX IF NOT EXISTS ""IX_Transactions_ChatId"" ON ""Transactions"" (""ChatId"");
                END $$;");

            using var seedScope = app.Services.CreateScope();
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedData.SeedAsync(seedDb);
            Console.WriteLine("Database ready.");
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DB setup attempt {i + 1} failed: {ex.Message}");
            if (i == 2) throw;
            await Task.Delay(3000);
        }
    }

    app.MapControllers();
    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FATAL: {ex}");
    throw;
}
