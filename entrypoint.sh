#!/bin/sh
set -e

export ConnectionStrings__DefaultConnection="Host=postgres;Port=5432;Database=salary_bot;Username=admin;Password=${DB_PASSWORD}"
export TELEGRAM__Token="${BOT_TOKEN}"
export TELEGRAM_BOT_TOKEN="${BOT_TOKEN}"

exec dotnet SalaryTelegramBot.Api.dll
