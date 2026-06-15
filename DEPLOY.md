# Деплой на бесплатный хостинг

## Вариант 1: Render.com (рекомендую)

1. Зарегистрируйтесь на [render.com](https://render.com)
2. Создайте новый **Web Service**
3. Подключите ваш GitHub репозиторий
4. Выберите **Docker** как метод деплоя
5. В переменных окружения укажите:
   - `TELEGRAM_BOT_TOKEN` — ваш токен бота
   - `TELEGRAM_ADMIN_USER_ID` — ваш Telegram ID
6. Создайте **Disk** (1GB) и примонтируйте к `/app/data`
7. Деплой запустится автоматически

**Бесплатный тир**: 750 часов/мес, автоматический засыпает после 15 мин без активности (но для бота это ок — он проснется при сообщении).

## Вариант 2: Railway.app

1. Зарегистрируйтесь на [railway.app](https://railway.com)
2. Создайте новый проект из GitHub
3. Railway автоматически определит `railway.json`
4. В переменных окружения укажите:
   - `TELEGRAM_BOT_TOKEN`
   - `TELEGRAM_ADMIN_USER_ID`
5. Деплой запустится автоматически

**Бесплатный тир**: $5 кредитов/мес (хватает на 24/7 работу бота).

## Вариант 3: Fly.io

1. Установите flyctl: `curl -L https://fly.io/install.sh | sh`
2. Войдите: `fly auth login`
3. Инициализируйте: `fly launch`
4. Создайте volume: `fly volumes create bot_data --size 1`
5. Деплой: `fly deploy`
6. Установите переменные:
   ```bash
   fly secrets set TELEGRAM_BOT_TOKEN="ваш_токен"
   fly secrets set TELEGRAM_ADMIN_USER_ID="ваш_id"
   ```

**Бесплатный тир**: 3共享 CPU + 256MB RAM бесплатно.

## Локальный запуск (без Docker)

```bash
dotnet run --project SalaryTelegramBot.Api
```

Бот запустится с SQLite базой в текущей директории (`salary_bot.db`).

## Важно

- Все варианты используют SQLite — данные хранятся в файле `salary_bot.db`
- Бэкап данных: просто скопируйте файл `salary_bot.db`
- Токен бота хранится в переменных окружения, не в коде
