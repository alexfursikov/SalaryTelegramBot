using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalaryTelegramBot.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccrualRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    DayOfMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccrualRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BotSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    CheckHour = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    IsNdflEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NdflStartDay = table.Column<int>(type: "INTEGER", nullable: true),
                    NdflStartMonth = table.Column<int>(type: "INTEGER", nullable: true),
                    NdflStartYear = table.Column<int>(type: "INTEGER", nullable: true),
                    CalculationStartMonth = table.Column<int>(type: "INTEGER", nullable: true),
                    CalculationStartYear = table.Column<int>(type: "INTEGER", nullable: true),
                    CalculationStartDay = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccrualRules_ChatId_DayOfMonth",
                table: "AccrualRules",
                columns: new[] { "ChatId", "DayOfMonth" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BotSettings_ChatId",
                table: "BotSettings",
                column: "ChatId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ChatId",
                table: "Transactions",
                column: "ChatId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccrualRules");

            migrationBuilder.DropTable(
                name: "BotSettings");

            migrationBuilder.DropTable(
                name: "Transactions");
        }
    }
}
