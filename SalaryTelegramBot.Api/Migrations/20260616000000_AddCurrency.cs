using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalaryTelegramBot.Api.Migrations
{
    public partial class AddCurrency : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "BotSettings",
                type: "text",
                nullable: false,
                defaultValue: "RUB");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "BotSettings");
        }
    }
}
