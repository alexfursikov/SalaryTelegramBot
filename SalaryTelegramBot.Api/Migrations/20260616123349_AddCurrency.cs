using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalaryTelegramBot.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "BotSettings",
                type: "text",
                nullable: false,
                defaultValue: "RUB");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "BotSettings");
        }
    }
}

