using System.ComponentModel.DataAnnotations.Schema;

namespace SalaryTelegramBot.Api.Models;

public class AccrualRule
{
    public int Id { get; set; }
    
    public long ChatId { get; set; }

    /// <summary>День месяца (1–31).</summary>
    public int DayOfMonth { get; set; }

    [NotMapped]
    public decimal Amount { get; set; }

    public byte[]? EncryptedAmount { get; set; }
}
