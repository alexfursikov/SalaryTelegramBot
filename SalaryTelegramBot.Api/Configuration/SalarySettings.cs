namespace SalaryTelegramBot.Api.Configuration;

public class SalarySettings
{
    /// <summary>Время ежедневной проверки (локальное время сервера).</summary>
    public int CheckHour { get; set; } = 12;

    public int CheckMinute { get; set; } = 0;

    /// <summary>Ставка НДФЛ, добавляемая сверху к каждому начислению.</summary>
    public decimal NdflPercent { get; set; } = 13m;

    /// <summary>Расписания начислений. Можно несколько записей с разными днями и суммами.</summary>
    public List<SalarySchedule> Schedules { get; set; } = [];
}

public class SalarySchedule
{
    /// <summary>Дни месяца (1–31), в которые начисляется зарплата.</summary>
    public int[] Days { get; set; } = [];

    public decimal Amount { get; set; }
}
