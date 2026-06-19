using System.Collections.Concurrent;

namespace SalaryTelegramBot.Api.Services;

public class UserKeyCache
{
    private readonly ConcurrentDictionary<long, (byte[] Key, DateTime Expires)> _cache = new();
    private readonly TimeSpan _sessionDuration = TimeSpan.FromMinutes(30);

    public void SetKey(long chatId, byte[] key)
        => _cache[chatId] = (key, DateTime.UtcNow + _sessionDuration);

    public byte[]? GetKey(long chatId)
    {
        if (_cache.TryGetValue(chatId, out var entry) && entry.Expires > DateTime.UtcNow)
            return entry.Key;
        _cache.TryRemove(chatId, out _);
        return null;
    }

    public void RemoveKey(long chatId) => _cache.TryRemove(chatId, out _);

    public bool HasKey(long chatId) => GetKey(chatId) != null;
}
