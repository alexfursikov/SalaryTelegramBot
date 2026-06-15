using System.Collections.Concurrent;

namespace SalaryTelegramBot.Api.Services;

public class RateLimiter
{
    private static readonly ConcurrentDictionary<long, DateTime> LastCommandTime = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(1);

    public bool IsAllowed(long chatId)
    {
        var now = DateTime.UtcNow;
        var last = LastCommandTime.AddOrUpdate(chatId, now, (_, _) => now);

        if (now - last < Cooldown)
            return false;

        LastCommandTime[chatId] = now;
        return true;
    }
}
