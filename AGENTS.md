# AGENTS.md

## Project Overview

Single-project .NET 8 ASP.NET Core Web API — a Telegram bot that tracks salary accruals, payments, and NDFL (Russian tax) per chat. All data is scoped by Telegram `chatId`.

## Stack

- **Runtime**: .NET 8.0.0 (`global.json` with `rollForward: latestMinor`)
- **Database**: SQLite (file `salary_bot.db`)
- **ORM**: Entity Framework Core 8 with SQLite
- **Bot**: Telegram.Bot v22 (polling, no webhook)
- **Container**: Docker multi-stage build (опционально)

## Build & Run

```bash
# Restore + build
dotnet build SalaryTelegramBot.sln

# Run locally (SQLite — никаких зависимостей)
dotnet run --project SalaryTelegramBot.Api

# Docker (опционально)
docker compose up --build
```

## Key Commands

```bash
# Create a new EF Core migration
dotnet ef migrations add <MigrationName> --project SalaryTelegramBot.Api

# Apply migrations manually (app does this automatically on startup)
dotnet ef database update --project SalaryTelegramBot.Api
```

There is **no test project, no linter, no formatter, no CI pipeline** in this repo.

## Environment Variables

Copy `.env.example` to `.env` and fill in:

| Variable | Purpose |
|---|---|
| `TELEGRAM_BOT_TOKEN` | Bot token from @BotFather |
| `TELEGRAM_ADMIN_USER_ID` | Telegram user ID for admin |

Для локального запуска переменные можно задать в `appsettings.json` или через переменные окружения.

## Architecture

- **Entry point**: `SalaryTelegramBot.Api/Program.cs` — registers DI, runs migrations, starts the app
- **Bot**: `Services/TelegramBotService.cs` — a `BackgroundService` that polls Telegram. Handles all commands and inline keyboard callbacks. Uses in-memory `ConcurrentDictionary` for per-user state machines (`BotAwaitState`)
- **Salary logic**: `Services/SalaryService.cs` — CRUD for transactions, balance/status calculations, NDFL auto-generation
- **Schedule logic**: `Services/SalaryScheduleService.cs` — manages accrual rules per chat, auto-seeds defaults from config, applies scheduled accruals
- **Models**: `Transaction` (Salary/Payment/Vat types), `AccrualRule` (day + amount per chat), `BotSettings` (per-chat config)
- **DB**: `Data/AppDbContext.cs` — three tables, unique indexes on `(ChatId, DayOfMonth)` for rules and `ChatId` for settings

## NDFL Logic

- Amounts in `AccrualRules` and salary transactions are **net** (на руки)
- NDFL is calculated as `(net / (1 - rate)) - net` and stored as a separate `Vat` transaction with `Comment = "auto_ndfl"`
- NDFL can be toggled on/off per chat and has a configurable start date
- `RecalculateNdflForRange` rebuilds auto_ndfl entries for a date range

## Деплой

Бесплатные варианты: Render.com, Railway.app, Fly.io. См. `DEPLOY.md` для инструкций.

## Gotchas

- **No authentication** — anyone who finds the bot can use it. Admin user ID is not enforced in code.
- **Migrations run on startup** — `Program.cs` calls `db.Database.Migrate()`. No manual step needed.
- **Default seeding** — `EnsureSeededForChatAsync` seeds `AccrualRules` and `BotSettings` from `appsettings.json` defaults on first interaction with a new chat.
- **DateTime handling** — all dates are normalized to UTC in the database. The `Transaction.Date` column has an explicit UTC conversion in `OnModelCreating`.
- **Day clamping** — rules for day 30/31 are clamped to `DaysInMonth` when applying accruals.
- **Bot state** — per-user conversation state is held in static `ConcurrentDictionary`. Lost on restart (acceptable for this use case).
- **appsettings.json contains a bot token** — this is committed. Use `.env` + environment variables for production.
