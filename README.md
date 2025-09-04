# WatchmenBot

Телеграм‑бот для ежедневной сводки переписки в группе. Собирает сообщения за день, считает активность участников и генерирует смешной/ироничный отчёт через Kimi2 (OpenAI‑совместимый API).

## Возможности
- Чтение сообщений из групп/супергрупп через **вебхуки** (HTTPS)
- Безопасная валидация запросов от Telegram (secret token + IP фильтрация)
- Хранение сообщений в **PostgreSQL** с использованием **Dapper**
- Ежедневная сводка за прошедший день в 00:05 по локальному времени
- Краткие выводы, забавные наблюдения и статистика активности

## Требования
- .NET 9 SDK
- **PostgreSQL** сервер
- Токен Telegram бота
- API ключ Kimi2 (например, OpenRouter). По умолчанию используется модель `moonshotai/kimi-k2` и базовый URL `https://openrouter.ai/api`
- **Публичный HTTPS-домен** для вебхука (порты 443, 80, 88, 8443)

## Установка и запуск
1. Склонируйте репозиторий и откройте папку проекта:
   ```bash
   cd WatchmenBot/WatchmenBot
   ```
2. Настройте PostgreSQL и создайте базу данных:
   ```sql
   CREATE DATABASE watchmenbot;
   ```
3. Укажите настройки в `appsettings.json`:
   - `Database:ConnectionString` — строка подключения к PostgreSQL
   - `Telegram:BotToken` — токен от @BotFather
   - `Telegram:WebhookUrl` — ваш публичный HTTPS URL (например, `https://yourdomain.com/telegram/update`)
   - `Telegram:WebhookSecret` — случайная строка 1-256 символов (A-Z, a-z, 0-9, _, -)
   - `Kimi:ApiKey` — ключ API
   - при необходимости `Kimi:BaseUrl`, `Kimi:Model`
4. В BotFather отключите Group Privacy (чтобы бот видел сообщения в группе).
5. Добавьте бота в тестовую группу.
6. **Деплой на HTTPS-сервер** (Azure, AWS, VPS с SSL/TLS).
7. Запустите бота — он автоматически создаст таблицы в PostgreSQL.
8. Установите вебхук через админ эндпоинт:
   ```bash
   curl -X POST "https://yourdomain.com/admin/set-webhook"
   ```

**Эндпоинты:**
- `/` — health check (`WatchmenBot is running`)
- `/health` — health check с проверкой PostgreSQL
- `/telegram/update` — приём апдейтов от Telegram (POST)
- `/admin/set-webhook` — установить вебхук (POST)
- `/admin/delete-webhook` — удалить вебхук (POST)
- `/admin/webhook-info` — информация о вебхуке (GET)

## Быстрое тестирование
- Напишите 10–20 сообщений в группе (желательно с разными типами: текст, ссылки, картинки).
- Чтобы не ждать ночи, можно временно изменить время запуска отчёта в `DailySummaryService` (по умолчанию 00:05) на ближайшие минуты и перезапустить.
- Либо дождитесь 00:05 — отчёт придёт за «вчера».

## Конфигурация
Файл `WatchmenBot/appsettings.json`:
```json
{
  "Database": {
    "ConnectionString": "Host=localhost;Database=watchmenbot;Username=postgres;Password=your_password"
  },
  "Telegram": {
    "BotToken": "YOUR_TELEGRAM_BOT_TOKEN",
    "WebhookUrl": "https://your-domain.com/telegram/update",
    "WebhookSecret": "GENERATE_RANDOM_SECRET_256_CHARS",
    "ValidateIpRange": true,
    "DeleteWebhookOnShutdown": false
  },
  "Kimi": {
    "ApiKey": "YOUR_KIMI_API_KEY",
    "BaseUrl": "https://openrouter.ai/api",
    "Model": "moonshotai/kimi-k2"
  },
}
```

## Структура

### Core
- `Program.cs` — минимальный стартап (13 строк)
- `Models/MessageRecord.cs` — модель сообщения

### Services
- `Services/MessageStore.cs` — хранилище PostgreSQL с Dapper
- `Services/DailySummaryService.cs` — ежедневная сводка
- `Services/KimiClient.cs` — клиент Kimi2 (OpenAI‑совместимый Chat Completions)

### Features (Clean Architecture)
- `Features/Webhook/ProcessTelegramUpdate.cs` — оркестратор обработки вебхука
- `Features/Messages/SaveMessage.cs` — сохранение сообщений в базу
- `Features/Admin/SetWebhook.cs` — установка вебхука
- `Features/Admin/DeleteWebhook.cs` — удаление вебхука  
- `Features/Admin/GetWebhookInfo.cs` — информация о вебхуке

### Infrastructure
- `Controllers/TelegramWebhookController.cs` — тонкий контроллер для вебхука
- `Controllers/TelegramAdminController.cs` — тонкий контроллер для админки
- `Extensions/ServiceCollectionExtensions.cs` — конфигурация DI
- `Extensions/WebApplicationExtensions.cs` — конфигурация приложения
- `Extensions/TelegramSecurityExtensions.cs` — валидация безопасности
- `Extensions/TelegramUpdateParserExtensions.cs` — парсинг апдейтов
- `Infrastructure/Database/` — PostgreSQL подключение и инициализация

## Деплой и безопасность

### Вебхук требования (Telegram)
- **HTTPS обязательно** — HTTP не поддерживается
- **Порты**: только 443, 80, 88, 8443
- **TLS 1.2+** — старые версии SSL/TLS отклоняются
- **IP диапазоны Telegram**: `149.154.160.0/20` и `91.108.4.0/22`

### Настройка секрета
Сгенерируйте случайный secret (1-256 символов):
```bash
# PowerShell
-join ((65..90) + (97..122) + (48..57) + 95 + 45 | Get-Random -Count 32 | % {[char]$_})

# Linux/Mac
openssl rand -base64 32 | tr -d "=+/" | cut -c1-32
```

### Управление вебхуком
После деплоя используйте админ эндпоинты:

**Установить вебхук:**
```bash
curl -X POST "https://yourdomain.com/admin/set-webhook"
```

**Проверить статус:**
```bash
curl "https://yourdomain.com/admin/webhook-info"
```

**Удалить вебхук:**
```bash
curl -X POST "https://yourdomain.com/admin/delete-webhook"
```

### Отладка
- Логи доступны через `ILogger<Program>`
- Неавторизованные запросы логируются как Warning
- Ошибки обработки — как Error

## PostgreSQL Setup

### Local Development:
```bash
# Docker
docker run --name watchmenbot-postgres -e POSTGRES_PASSWORD=dev_password -e POSTGRES_DB=watchmenbot_dev -p 5432:5432 -d postgres:16

# Or install PostgreSQL locally and create database
psql -U postgres -c "CREATE DATABASE watchmenbot_dev;"
```

### Production:
- Use managed PostgreSQL (Azure Database, AWS RDS, Google Cloud SQL)
- Or self-hosted with proper backup strategy

## Примечания
- `appsettings.Development.json` игнорируется Git и может содержать локальные секреты.
- Таблицы создаются автоматически при первом запуске (`DatabaseInitializer`).
- Используется составной PRIMARY KEY (`chat_id`, `id`) для уникальности сообщений.
- По умолчанию используется OpenRouter (`moonshotai/kimi-k2`). При желании можно указать свой эндпоинт Kimi. 