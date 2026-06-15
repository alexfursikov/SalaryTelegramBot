using System.Collections.Concurrent;
using System.Text.Json;

namespace SalaryTelegramBot.Api.Services;

public class BotStateService : IDisposable
{
    private readonly string _stateFile;
    private readonly ConcurrentDictionary<long, string> _states = new();
    private readonly ConcurrentDictionary<long, decimal> _pendingAmounts = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _saveTimer;

    public BotStateService(IWebHostEnvironment env)
    {
        _stateFile = Path.Combine(env.ContentRootPath, "bot_state.json");
        Load();
        _saveTimer = new Timer(_ => _ = SaveAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public string? GetState(long chatId) =>
        _states.TryGetValue(chatId, out var state) ? state : null;

    public void SetState(long chatId, string state) => _states[chatId] = state;

    public void RemoveState(long chatId) => _states.TryRemove(chatId, out _);

    public decimal? GetPendingAmount(long chatId) =>
        _pendingAmounts.TryGetValue(chatId, out var amount) ? amount : null;

    public void SetPendingAmount(long chatId, decimal amount) => _pendingAmounts[chatId] = amount;

    public void RemovePendingAmount(long chatId) => _pendingAmounts.TryRemove(chatId, out _);

    public void ClearAll(long chatId)
    {
        _states.TryRemove(chatId, out _);
        _pendingAmounts.TryRemove(chatId, out _);
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        _lock.Dispose();
    }

    private async Task SaveAsync()
    {
        if (_states.IsEmpty && _pendingAmounts.IsEmpty)
            return;

        await _lock.WaitAsync();
        try
        {
            var data = new StateData
            {
                States = _states.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                PendingAmounts = _pendingAmounts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_stateFile, json);
        }
        catch
        {
            // Best effort — state is still in memory
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Load()
    {
        if (!File.Exists(_stateFile))
            return;

        try
        {
            var json = File.ReadAllText(_stateFile);
            var data = JsonSerializer.Deserialize<StateData>(json);
            if (data is null) return;

            foreach (var kv in data.States)
                if (long.TryParse(kv.Key, out var id))
                    _states[id] = kv.Value;

            foreach (var kv in data.PendingAmounts)
                if (long.TryParse(kv.Key, out var id))
                    _pendingAmounts[id] = kv.Value;

            File.Delete(_stateFile);
        }
        catch
        {
            // Ignore corrupt state file on startup
        }
    }

    private class StateData
    {
        public Dictionary<string, string> States { get; set; } = new();
        public Dictionary<string, decimal> PendingAmounts { get; set; } = new();
    }
}
