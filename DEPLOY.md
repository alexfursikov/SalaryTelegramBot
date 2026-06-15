# Деплой на бесплатный хостинг

## Шаг 1: Создай базу данных на Supabase (бесплатно, без expiration)

1. Зайди на [supabase.com](https://supabase.com) и зарегистрируйся через GitHub
2. Нажми **New project**
3. Настройки:
   - **Organization:** выбери или создай
   - **Project name:** `salary-bot`
   - **Database Password:** придумай пароль (сохрани!)
   - **Region:** выбери ближайшую (Europe West)
4. Нажми **Create new project** (~2 мин на создание)
5. Перейди в **Settings** → **Database**
6. Скопируй **Connection string** → **URI** (выглядит как `postgresql://postgres.xxxx:PASSWORD@aws-0-eu-west.pooler.supabase.com:6543/postgres`)

**Важно:** используй **Pooler** connection string (порт 6543), а не Direct (порт 5432). Pooler поддерживает больше одновременных подключений.

## Шаг 2: Создай Web Service на Render

1. Зайди на [render.com](https://render.com)
2. Нажми **New +** → **Web Service**
3. Подключи репозиторий `SalaryTelegramBot`
4. Настройки:
   - **Name:** `salary-telegram-bot`
   - **Runtime:** `Docker`
   - **DockerfilePath:** `SalaryTelegramBot.Api/Dockerfile`
5. В **Environment Variables** добавь:
   - `TELEGRAM_BOT_TOKEN` = твой токен
   - `TELEGRAM_ADMIN_USER_ID` = `454887189`
   - `ConnectionStrings__DefaultConnection` = URI из Supabase
6. Нажми **Create Web Service**

**Бесплатный тир Render**: 750 часов/мес, засыпает после 15 мин без активности (просыпается при сообщении ~30-50 сек).

## Альтернативы

### Railway.app
1. Зарегистрируйтесь на [railway.app](https://railway.com)
2. Создайте проект из GitHub
3. Добавьте переменные окружения (те же)
4. **Бесплатный тир**: $5 кредитов/мес

### Fly.io
1. `curl -L https://fly.io/install.sh | sh`
2. `fly auth login` → `fly launch` → `fly deploy`
3. `fly secrets set` для переменных
4. **Бесплатный тир**: 3 shared CPU + 256MB RAM

## Локальный запуск

### С Docker (рекомендовано)
```bash
docker compose up --build
```

### Без Docker
```bash
dotnet run --project SalaryTelegramBot.Api
```
Требует PostgreSQL на порту 55432 (из docker-compose).

## Важно

- Supabase бесплатный — **данные не удаляются** автоматически
- Бэкоп: в Supabase → Database → Backups (автоматические ежедневные)
- Токен бота хранится в переменных окружения, не в коде
