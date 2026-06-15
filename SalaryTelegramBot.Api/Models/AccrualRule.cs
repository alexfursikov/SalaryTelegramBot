namespace SalaryTelegramBot.Api.Models;

public class AccrualRule
{
    public int Id { get; set; }
    
    public long ChatId { get; set; }

    /// <summary>День месяца (1–31).</summary>
    public int DayOfMonth { get; set; }

    public decimal Amount { get; set; }
}
