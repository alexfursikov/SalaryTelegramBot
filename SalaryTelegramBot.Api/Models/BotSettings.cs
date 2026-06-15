namespace SalaryTelegramBot.Api.Models;

public class BotSettings
{
    public int Id { get; set; }

    public long ChatId { get; set; }

    public int CheckHour { get; set; } = 12;

    public int CheckMinute { get; set; } = 0;

    public bool IsNdflEnabled { get; set; } = true;

    public int? NdflStartDay { get; set; }

    public int? NdflStartMonth { get; set; }

    public int? NdflStartYear { get; set; }

    public int? CalculationStartMonth { get; set; }

    public int? CalculationStartYear { get; set; }

    public int? CalculationStartDay { get; set; }
}
