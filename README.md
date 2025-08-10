# WatchmenBot

Телеграм‑бот для ежедневной сводки переписки в группе. Собирает сообщения за день, считает активность участников и генерирует смешной/ироничный отчёт через Kimi2 (OpenAI‑совместимый API).

## Возможности
- Чтение сообщений из групп/супергрупп (long‑polling)
- Хранение сообщений в LiteDB (`Data/bot.db`)
- Ежедневная сводка за прошедший день в 00:05 по локальному времени
- Краткие выводы, забавные наблюдения и статистика активности

## Требования
- .NET 9 SDK
- Токен Telegram бота
- API ключ Kimi2 (например, OpenRouter). По умолчанию используется модель `moonshotai/kimi-k2` и базовый URL `https://openrouter.ai/api`.

## Установка и запуск
1. Склонируйте репозиторий и откройте папку проекта:
   ```bash
   cd WatchmenBot/WatchmenBot
   ```
2. Укажите настройки в `appsettings.json`:
   - `Telegram:BotToken`
   - `Kimi:ApiKey`
   - при необходимости `Kimi:BaseUrl`, `Kimi:Model`
3. Отключите вебхук (для long‑polling):
   - `https://api.telegram.org/bot<ТОКЕН>/deleteWebhook`
4. В BotFather отключите Group Privacy (чтобы бот видел сообщения в группе).
5. Добавьте бота в тестовую группу.
6. Запустите бота:
   ```bash
   dotnet run
   ```

Маршрут `/` вернёт `WatchmenBot is running`.

## Быстрое тестирование
- Напишите 10–20 сообщений в группе (желательно с разными типами: текст, ссылки, картинки).
- Чтобы не ждать ночи, можно временно изменить время запуска отчёта в `DailySummaryService` (по умолчанию 00:05) на ближайшие минуты и перезапустить.
- Либо дождитесь 00:05 — отчёт придёт за «вчера».

## Конфигурация
Файл `WatchmenBot/appsettings.json`:
```json
{
  "Telegram": {
    "BotToken": "YOUR_TELEGRAM_BOT_TOKEN"
  },
  "Kimi": {
    "ApiKey": "YOUR_KIMI_API_KEY",
    "BaseUrl": "https://openrouter.ai/api",
    "Model": "moonshotai/kimi-k2"
  },
  "Storage": {
    "LiteDbPath": "Data/bot.db"
  }
}
```

## Структура
- `Models/MessageRecord.cs` — модель сообщения
- `Services/MessageStore.cs` — хранилище LiteDB
- `Services/TelegramBotRunner.cs` — приём апдейтов и сохранение
- `Services/DailySummaryService.cs` — ежедневная сводка
- `Services/KimiClient.cs` — клиент Kimi2 (OpenAI‑совместимый Chat Completions)
- `Program.cs` — DI/настройка

## Примечания
- `appsettings.Development.json` игнорируется Git и может содержать локальные секреты.
- База `Data/bot.db` не коммитится.
- По умолчанию используется OpenRouter (`moonshotai/kimi-k2`). При желании можно указать свой эндпоинт Kimi. 