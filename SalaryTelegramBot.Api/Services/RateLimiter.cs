using System.Collections.Concurrent;

namespace SalaryTelegramBot.Api.Services;

public class RateLimiter
{
    private readonly ConcurrentDictionary<long, DateTime> _lastCommandTime = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(1);

    public bool IsAllowed(long chatId)
    {
        var now = DateTime.UtcNow;

        var last = _lastCommandTime.AddOrUpdate(
            chatId,
            now,
            (_, existing) => now - existing >= Cooldown ? now : existing);

        return _lastCommandTime.TryGetValue(chatId, out var stored) && stored == now;
    }
}
