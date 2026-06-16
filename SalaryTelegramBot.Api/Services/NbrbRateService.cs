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

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var response = await _http.GetAsync(
                "https://open.er-api.com/v6/latest/BYN", cts.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var rates = root.GetProperty("rates");
            if (rates.TryGetProperty(currency, out var rateElement))
            {
                var ratePerByn = rateElement.GetDecimal();
                if (ratePerByn > 0)
                {
                    var result = 1m / ratePerByn;
                    _logger.LogInformation("Rate for {Currency}: {Rate} BYN", currency, result);
                    return result;
                }
            }

            _logger.LogWarning("Currency {Currency} not found in rates", currency);
            return null;
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
