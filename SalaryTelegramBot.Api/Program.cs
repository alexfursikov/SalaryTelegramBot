using Microsoft.EntityFrameworkCore;
using SalaryTelegramBot.Api.Configuration;
using SalaryTelegramBot.Api.Data;
using SalaryTelegramBot.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SalarySettings>(
    builder.Configuration.GetSection("Salary"));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Database connection string not configured. Set ConnectionStrings:DefaultConnection in appsettings or ConnectionStrings__DefaultConnection env var.");
}

builder.Services.AddDbContext<AppDbContext>(x => x.UseNpgsql(connectionString));

builder.Services.AddScoped<SalaryService>();
builder.Services.AddScoped<SalaryScheduleService>();

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
