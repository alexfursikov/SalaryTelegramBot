using System.Text.Json;

namespace SalaryTelegramBot.Api.Services;

public class NbrbRateService
{
    private readonly HttpClient _http;
    private readonly ILogger<NbrbRateService> _logger;

    public NbrbRateService(HttpClient http, ILogger<NbrbRateService> logger)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _logger = logger;
    }

    public async Task<decimal?> GetRateAsync(string currency)
    {
        if (currency == "BYN")
            return 1m;

        var curId = currency switch
        {
            "USD" => 431,
            "EUR" => 451,
            "RUB" => 456,
            _ => 0
        };

        if (curId == 0)
            return null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var response = await _http.GetAsync(
                $"https://www.nbrb.by/api/exrates/rates/{curId}?format=json", cts.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var rate = root.GetProperty("Cur_OfficialRate").GetDecimal();
            var scale = root.GetProperty("Cur_Scale").GetInt32();

            _logger.LogInformation("НБ РБ rate for {Currency}: {Rate} per {Scale}", currency, rate, scale);
            return rate / scale;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch rate for {Currency}", currency);
            return null;
        }
    }

    public async Task<(decimal? FromRate, decimal? ToRate)> GetRatesAsync(string fromCurrency, string toCurrency)
    {
        var fromTask = GetRateAsync(fromCurrency);
        var toTask = GetRateAsync(toCurrency);
        await Task.WhenAll(fromTask, toTask);
        return (fromTask.Result, toTask.Result);
    }

    public decimal Convert(decimal amount, decimal fromRate, decimal toRate)
    {
        if (toRate == 0)
            return amount;
        return Math.Round(amount * fromRate / toRate, 2, MidpointRounding.AwayFromZero);
    }
}
