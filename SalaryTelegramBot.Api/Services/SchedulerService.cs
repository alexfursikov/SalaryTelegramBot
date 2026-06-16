using Microsoft.EntityFrameworkCore;

namespace SalaryTelegramBot.Api.Services;

public class SchedulerService : BackgroundService
{
    private readonly IServiceProvider _provider;
    private readonly ILogger<SchedulerService> _logger;

    public SchedulerService(IServiceProvider provider, ILogger<SchedulerService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _provider.CreateScope();
                var scheduleService = scope.ServiceProvider.GetRequiredService<SalaryScheduleService>();
                var salaryService = scope.ServiceProvider.GetRequiredService<SalaryService>();

                await scheduleService.ApplyRulesForCurrentTimeAsync(salaryService);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scheduled accrual check");
            }

            try
            {
                using var scope = _provider.CreateScope();
                var reminderService = scope.ServiceProvider.GetRequiredService<ReminderService>();
                await reminderService.CheckAndSendRemindersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during reminder check");
            }

            var now = DateTime.Now;
            var nextRun = now.Date.AddDays(1).AddSeconds(5);
            await Task.Delay(nextRun - now, stoppingToken);
        }
    }
}
