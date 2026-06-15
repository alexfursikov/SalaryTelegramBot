namespace SalaryTelegramBot.Api.Models;

public class Transaction
{
    public int Id { get; set; }
    
    public long ChatId { get; set; }

    public DateTime Date { get; set; }

    public decimal Amount { get; set; }

    public TransactionType Type { get; set; }

    public string Comment { get; set; } = "";
}
