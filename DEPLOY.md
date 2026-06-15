# Деплой на бесплатный хостинг

## Вариант 1: Render.com (рекомендую)

### Шаг 1: Создай PostgreSQL базу данных

1. Зайди на [render.com](https://render.com)
2. Нажми **New +** → **PostgreSQL**
3. Настройки:
   - **Name:** `salary-postgres`
   - **Database:** `salary_bot`
   - **User:** `postgres`
   - **Plan:** Free
4. Нажми **Create Database**
5. Скопируй **Internal Database URL** (выглядит как `postgres://postgres:...@...`)

### Шаг 2: Создай Web Service

1. Нажми **New +** → **Web Service**
2. Подключи репозиторий `SalaryTelegramBot`
3. Настройки:
   - **Name:** `salary-telegram-bot`
   - **Runtime:** `Docker`
   - **DockerfilePath:** `SalaryTelegramBot.Api/Dockerfile`
4. В **Environment Variables** добавь:
   - `TELEGRAM_BOT_TOKEN` = твой токен
   - `TELEGRAM_ADMIN_USER_ID` = `454887189`
   - `ConnectionStrings__DefaultConnection` = URL из шага 1
5. Нажми **Create Web Service**

**Бесплатный тир**: 750 часов/мес, автоматический засыпает после 15 мин без активности.

## Вариант 2: Railway.app

1. Зарегистрируйтесь на [railway.app](https://railway.com)
2. Создайте новый проект из GitHub
3. Добавьте PostgreSQL сервис
4. В переменных окружения укажите:
   - `TELEGRAM_BOT_TOKEN`
   - `TELEGRAM_ADMIN_USER_ID`
   - `ConnectionStrings__DefaultConnection` = URL PostgreSQL
5. Деплой запустится автоматически

**Бесплатный тир**: $5 кредитов/мес (хватает на 24/7 работу бота).

## Вариант 3: Fly.io

1. Установите flyctl: `curl -L https://fly.io/install.sh | sh`
2. Войдите: `fly auth login`
3. Инициализируйте: `fly launch`
4. Добавьте PostgreSQL: `fly postgres create`
5. Деплой: `fly deploy`
6. Установите переменные:
   ```bash
   fly secrets set TELEGRAM_BOT_TOKEN="ваш_токен"
   fly secrets set TELEGRAM_ADMIN_USER_ID="ваш_id"
   fly secrets set ConnectionStrings__DefaultConnection="ваш_url"
   ```

**Бесплатный тир**: 3共享 CPU + 256MB RAM бесплатно.

## Локальный запуск

### С Docker (рекомендовано)
```bash
docker compose up --build
```
Бот + PostgreSQL запустятся локально. Бот будет доступен на `http://localhost:5116`.

### Без Docker
```bash
dotnet run --project SalaryTelegramBot.Api
```
Требует PostgreSQL на порту 55432.

## Важно

- Данные хранятся в PostgreSQL — перезапуск/деплой не удаляет их
- Бэкоп: `pg_dump -U postgres salary_bot > backup.sql`
- Токен бота хранится в переменных окружения, не в коде
