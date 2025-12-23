# WatchmenBot

**Telegram-бот для анализа и суммирования группового общения с использованием AI и векторного поиска.**

WatchmenBot сохраняет историю сообщений группы, создаёт семантические embeddings и использует их для умных выжимок, поиска и ответов на вопросы о контексте чата.

---

## Возможности

### Для пользователей

| Команда | Описание |
|---------|----------|
| `/summary [N]` | Выжимка чата за N часов (по умолчанию 24). Форматы: `48`, `48h`, `2d` |
| `/ask <вопрос>` | Вопрос по истории чата — про людей, события, обсуждения (RAG + Grok) |
| `/smart <вопрос>` | Умный поиск в интернете (Perplexity, без контекста чата) |
| `/search <запрос>` | Семантический поиск по истории чата |
| `/recall @username` | Все сообщения пользователя за последние 7 дней |
| `/truth [N]` | Fact-check последних N сообщений (по умолчанию 5) |

### Автоматические функции

- **Ежедневная выжимка** — отправляется в настроенное время (по умолчанию 21:00)
- **Фоновая индексация** — все сообщения автоматически получают embeddings
- **Отчёт администратору** — ежедневная статистика использования

### Для администратора

Полный набор команд через `/admin`:

```
/admin status              — текущие настройки
/admin report              — отчёт сейчас
/admin chats               — список известных чатов
/admin import <chat_id>    — инструкции по импорту истории
/admin llm                 — список LLM провайдеров
/admin llm_set <name>      — сменить дефолтный провайдер
/admin llm_on/off <name>   — включить/выключить провайдера
/admin llm_test [name]     — тест провайдера
/admin prompts             — показать все промпты
/admin prompt <cmd>        — показать промпт команды
/admin prompt_tag <cmd> <tag>  — установить LLM тег для команды
/admin prompt_reset <cmd>  — сбросить промпт на дефолт
/admin names <chat_id>     — список имён в чате
/admin rename <chat_id> "Старое" "Новое"  — переименовать
/admin set_summary_time HH:mm   — время выжимки
/admin set_report_time HH:mm    — время отчёта
/admin set_timezone +N          — часовой пояс
/admin reindex [chat_id|all] confirm  — переиндексация embeddings
```

**Загрузка файлов:**
- ZIP-архив (экспорт из Telegram Desktop) для импорта истории
- TXT-файл для кастомных промптов

---

## Архитектура

### Обзор

```
┌─────────────────────────────────────────────────────────────────┐
│                        Telegram Bot API                         │
│                         (Webhook/Polling)                        │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                      ASP.NET Core WebAPI                         │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │              TelegramWebhookController                       │ │
│  │         (валидация secret token + IP range)                  │ │
│  └──────────────────────────┬──────────────────────────────────┘ │
│                             │                                    │
│                             ▼                                    │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │               ProcessTelegramUpdate                          │ │
│  │            (роутинг по типу сообщения)                       │ │
│  └──────────────────────────┬──────────────────────────────────┘ │
│                             │                                    │
│           ┌─────────────────┼─────────────────┐                  │
│           ▼                 ▼                 ▼                  │
│  ┌─────────────┐   ┌─────────────┐   ┌─────────────┐             │
│  │  Commands   │   │   Messages  │   │    Admin    │             │
│  │  Handlers   │   │   Handler   │   │   Handler   │             │
│  └──────┬──────┘   └──────┬──────┘   └─────────────┘             │
│         │                 │                                      │
│         ▼                 ▼                                      │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │                      Services Layer                          │ │
│  │  ┌───────────┐ ┌───────────┐ ┌───────────┐                   │ │
│  │  │ LlmRouter │ │ Embedding │ │  Message  │                   │ │
│  │  │           │ │  Service  │ │   Store   │                   │ │
│  │  └─────┬─────┘ └─────┬─────┘ └─────┬─────┘                   │ │
│  └────────┼─────────────┼─────────────┼────────────────────────┘ │
│           │             │             │                          │
└───────────┼─────────────┼─────────────┼──────────────────────────┘
            │             │             │
            ▼             ▼             ▼
┌───────────────┐ ┌───────────────┐ ┌───────────────────────────┐
│   OpenRouter  │ │    OpenAI     │ │       PostgreSQL          │
│  (LLM API)    │ │  (Embeddings) │ │  + pgvector extension     │
└───────────────┘ └───────────────┘ └───────────────────────────┘
```

### Структура проекта

```
WatchmenBot/
├── Program.cs                      # Точка входа
├── appsettings.json                # Конфигурация
│
├── Controllers/
│   ├── TelegramWebhookController   # Приём webhook от Telegram
│   └── TelegramAdminController     # Административные эндпоинты
│
├── Features/                       # Бизнес-логика (Clean Architecture)
│   ├── Webhook/
│   │   └── ProcessTelegramUpdate   # Роутинг обновлений
│   ├── Messages/
│   │   └── SaveMessage             # Сохранение сообщений
│   ├── Summary/
│   │   └── GenerateSummary         # Генерация выжимок
│   ├── Search/
│   │   ├── SearchHandler           # /search
│   │   ├── AskHandler              # /ask и /smart
│   │   ├── RecallHandler           # /recall
│   │   └── FactCheckHandler        # /truth
│   └── Admin/
│       └── AdminCommandHandler     # Все /admin команды
│
├── Services/
│   ├── LLM/
│   │   ├── ILlmProvider            # Интерфейс провайдера
│   │   ├── OpenAiCompatibleProvider # OpenAI-совместимые API
│   │   ├── LlmRouter               # Маршрутизация по тегам
│   │   └── LlmProviderFactory      # Фабрика провайдеров
│   ├── EmbeddingClient             # HTTP-клиент для OpenAI
│   ├── EmbeddingService            # Управление embeddings
│   ├── MessageStore                # CRUD сообщений + поиск
│   ├── SmartSummaryService         # Умные выжимки с темами
│   ├── DailySummaryService         # Фоновый сервис выжимок
│   ├── DailyLogReportService       # Отчёты администратору
│   ├── BackgroundEmbeddingService  # Фоновая индексация
│   ├── OpenRouterUsageService      # Отслеживание баланса API
│   ├── ChatImportService           # Импорт истории
│   ├── TelegramExportParser        # Парсинг экспорта Telegram
│   ├── AdminSettingsStore          # Настройки в БД
│   ├── PromptSettingsStore         # Промпты в БД
│   └── LogCollector                # Сбор логов
│
├── Models/
│   └── MessageRecord               # DTO сообщения
│
├── Infrastructure/
│   └── Database/
│       ├── IDbConnectionFactory    # Интерфейс подключения
│       ├── PostgreSqlConnectionFactory
│       ├── DatabaseInitializer     # Автосоздание таблиц
│       └── DatabaseHealthCheck     # Health check
│
└── Extensions/
    ├── ServiceCollectionExtensions # DI конфигурация
    ├── TelegramSecurityExtensions  # Валидация безопасности
    └── TelegramUpdateParserExtensions
```

### Ключевые компоненты

#### LLM Router (многопровайдерная архитектура)

Бот не привязан к одному LLM провайдеру. Поддерживается:

- **Маршрутизация по тегам** — каждая команда может использовать свой провайдер
- **Fallback по приоритету** — автоматическое переключение при ошибках
- **Динамическое управление** — включение/отключение провайдеров на лету

```
/ask     →  tag: "uncensored"  →  Grok (дерзкие ответы)
/smart   →  tag: "factcheck"   →  Perplexity (поиск в интернете)
/summary →  tag: "default"     →  Grok или Qwen (по приоритету)
```

#### RAG (Retrieval-Augmented Generation)

Все ответы основаны на реальном контексте чата:

1. **Сохранение** — каждое сообщение сохраняется в PostgreSQL
2. **Индексация** — фоновый сервис создаёт embeddings (text-embedding-3-small)
3. **Поиск** — семантически похожие сообщения через pgvector (cosine similarity)
4. **Генерация** — найденный контекст передаётся в LLM для ответа

#### Smart Summary (умные выжимки)

Двухэтапный процесс:

1. **Извлечение тем** — LLM выделяет 3-7 основных топиков из сообщений
2. **Поиск контекста** — для каждой темы ищутся релевантные сообщения
3. **Генерация** — сначала факты (temp 0.3), потом юмор (temp 0.6)

#### Группировка сообщений

Для экономии на API и улучшения качества:

- Последовательные сообщения одного пользователя объединяются
- Окно: 5 минут, максимум 10 сообщений
- Metadata (автор, время) сохраняется в JSONB

---

## Алгоритмы генерации ответов

### Обзор LLM и Embeddings

| Компонент | Провайдер | Модель | Назначение |
|-----------|-----------|--------|------------|
| **Embeddings** | OpenAI | `text-embedding-3-small` | Векторизация сообщений (1536 dims) |
| **LLM Default** | OpenRouter | `x-ai/grok-4-fast` | Основные запросы, креатив |
| **LLM Uncensored** | OpenRouter | `x-ai/grok-4-fast` | Дерзкие ответы без цензуры |
| **LLM Factcheck** | OpenRouter | `perplexity/sonar` | Поиск в интернете, проверка фактов |
| **LLM Cheap** | OpenRouter | `qwen/qwen-2.5-72b-instruct` | Экономичные запросы |
| **LLM Fallback** | OpenRouter | `meta-llama/llama-3.3-70b-instruct` | Резервный провайдер |

### Embeddings (OpenAI)

```
Сообщение → OpenAI API → vector(1536) → PostgreSQL + pgvector
                         ↓
              Cosine Similarity Search
```

**Параметры:**
- **Модель**: `text-embedding-3-small`
- **Размерность**: 1536
- **Batch size**: 50 сообщений
- **Группировка**: последовательные сообщения одного юзера (окно 5 мин, макс 10)

---

### `/summary` — Умная выжимка

```
┌─────────────────────────────────────────────────────────────────┐
│  1. СБОР ДАННЫХ                                                 │
│     messages table → фильтрация ботов → human messages          │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  2. РАЗНООБРАЗНАЯ ВЫБОРКА (Embeddings)                          │
│     GetDiverseMessagesAsync() → 100 разных сообщений            │
│     (максимально охватывает все темы периода)                   │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  3. ИЗВЛЕЧЕНИЕ ТЕМ                                              │
│     LLM (temp=0.3) → JSON: ["тема1", "тема2", ...]              │
│     Провайдер: default (Grok/Qwen)                              │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  4. ПОИСК КОНТЕКСТА ПО ТЕМАМ                                    │
│     Для каждой темы:                                            │
│       SearchSimilarInRangeAsync(тема, limit=25)                 │
│       → релевантные сообщения (similarity > 0.25)               │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  5. ДВУХЭТАПНАЯ ГЕНЕРАЦИЯ                                       │
│                                                                 │
│     Stage 1: ФАКТЫ (temp=0.3)                                   │
│     → Точное извлечение: кто, что, когда, цитаты                │
│     Провайдер: default (дешёвый)                                │
│                                                                 │
│     Stage 2: ЮМОР (temp=0.6)                                    │
│     → Саркастичная подача тех же фактов                         │
│     Провайдер: по тегу команды (default)                        │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  РЕЗУЛЬТАТ: Структурированная выжимка                           │
│  🔥 Главное → 😂 Лучшие моменты → 💬 Темы → 🏆 Герои → 🎭 Итог │
└─────────────────────────────────────────────────────────────────┘
```

**LLM**: `default` (Grok) или `summary` тег
**Temperature**: 0.3 (факты) → 0.6 (юмор)

---

### `/ask` — Вопрос по истории чата

```
┌─────────────────────────────────────────────────────────────────┐
│  1. ОПРЕДЕЛЕНИЕ ТИПА ВОПРОСА                                    │
│     DetectPersonalQuestion(вопрос)                              │
│     → "self" (про себя) / "@username" / null (общий)            │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  2. ПОИСК КОНТЕКСТА                                             │
│                                                                 │
│     Если "я ...?" или "@user":                                  │
│       GetPersonalContextAsync() → сообщения юзера + упоминания  │
│                                                                 │
│     Иначе:                                                      │
│       SearchWithConfidenceAsync() → RAG поиск + гейт уверенности│
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  3. ДВУХЭТАПНАЯ ГЕНЕРАЦИЯ                                       │
│                                                                 │
│     Stage 1: ФАКТЫ (temp=0.3)                                   │
│     → Точные факты из контекста                                 │
│                                                                 │
│     Stage 2: ПОДЪЁБКА (temp=0.6)                                │
│     → Едкий юмор с матом                                        │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  РЕЗУЛЬТАТ: Дерзкий ответ на основе истории чата                │
└─────────────────────────────────────────────────────────────────┘
```

**LLM**: `uncensored` тег → Grok (без цензуры)
**Temperature**: 0.3 → 0.6
**Требует**: контекст из embeddings (иначе "не нашёл")

---

### `/smart` — Поиск в интернете

```
┌─────────────────────────────────────────────────────────────────┐
│  БЕЗ ПОИСКА ПО ЧАТУ                                             │
│  Прямой запрос к Perplexity                                     │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  ГЕНЕРАЦИЯ ОТВЕТА                                               │
│  LLM с поиском в интернете                                      │
│  Prompt: вопрос + дата                                          │
│  → Ответ с источниками                                          │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  РЕЗУЛЬТАТ: Информативный ответ + ссылки на источники           │
└─────────────────────────────────────────────────────────────────┘
```

**LLM**: `factcheck` тег → Perplexity (поиск в интернете)
**Temperature**: 0.5
**НЕ использует**: контекст чата

---

### `/search` — Семантический поиск

```
┌─────────────────────────────────────────────────────────────────┐
│  1. ВЕКТОРИЗАЦИЯ ЗАПРОСА                                        │
│     OpenAI Embeddings API → vector(1536)                        │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  2. ПОИСК В PGVECTOR                                            │
│     SELECT ... ORDER BY embedding <=> query_vector LIMIT 1      │
│     (cosine similarity)                                         │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  РЕЗУЛЬТАТ: Самое релевантное сообщение                         │
└─────────────────────────────────────────────────────────────────┘
```

**LLM**: не используется
**Embeddings**: OpenAI `text-embedding-3-small`

---

### `/truth` — Fact-check

```
┌─────────────────────────────────────────────────────────────────┐
│  1. ПОЛУЧЕНИЕ СООБЩЕНИЙ                                         │
│     GetLatestMessagesAsync(chat_id, N)                          │
│     → Последние N сообщений из БД                               │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  2. ПРОВЕРКА ФАКТОВ                                             │
│     LLM с поиском в интернете                                   │
│     Prompt: "Проверь факты, укажи кто прав/не прав"             │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  РЕЗУЛЬТАТ:                                                     │
│  ✅ [факт] — верно                                              │
│  ❌ [имя] не прав: [почему]                                     │
│  🤷 [что-то] — не проверить                                     │
└─────────────────────────────────────────────────────────────────┘
```

**LLM**: `factcheck` тег → Perplexity (поиск в интернете)
**Temperature**: 0.5
**Embeddings**: не используется

---

### `/recall` — История пользователя

```
┌─────────────────────────────────────────────────────────────────┐
│  1. ПОИСК СООБЩЕНИЙ                                             │
│     SELECT * FROM messages                                      │
│     WHERE from_user_name = @username                            │
│     AND date_utc > NOW() - 7 days                               │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  2. ГРУППИРОВКА                                                 │
│     По дням → до 5 дней × 10 сообщений                          │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  РЕЗУЛЬТАТ: Хронология сообщений пользователя                   │
└─────────────────────────────────────────────────────────────────┘
```

**LLM**: не используется
**Embeddings**: не используется

---

### Сводная таблица

| Команда | Embeddings | LLM | Тег | Temp | Этапов |
|---------|:----------:|:---:|:---:|:----:|:------:|
| `/summary` | поиск тем | Grok/Qwen | default | 0.3→0.6 | 2 |
| `/ask` | RAG + персональный | Grok | uncensored | 0.3→0.6 | 2 |
| `/smart` | ❌ нет | Perplexity | factcheck | 0.5 | 1 |
| `/search` | поиск | — | — | — | 0 |
| `/truth` | — | Perplexity | factcheck | 0.5 | 1 |
| `/recall` | — | — | — | — | 0 |

---

## База данных

### Схема

```sql
-- Основные сообщения
CREATE TABLE messages (
    chat_id BIGINT NOT NULL,
    id BIGINT NOT NULL,
    date_utc TIMESTAMP NOT NULL,
    from_user_id BIGINT,
    from_user_name TEXT,
    text TEXT,
    PRIMARY KEY (chat_id, id)
);

-- Векторные embeddings (pgvector)
CREATE TABLE message_embeddings (
    id SERIAL PRIMARY KEY,
    chat_id BIGINT NOT NULL,
    message_ids BIGINT[] NOT NULL,
    embedding vector(1536) NOT NULL,
    text TEXT NOT NULL,
    metadata JSONB,
    created_at TIMESTAMP DEFAULT NOW()
);

-- Информация о чатах
CREATE TABLE chats (
    id BIGINT PRIMARY KEY,
    title TEXT,
    type TEXT,
    updated_at TIMESTAMP DEFAULT NOW()
);

-- Настройки администратора
CREATE TABLE admin_settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TIMESTAMP DEFAULT NOW()
);

-- Кастомные промпты
CREATE TABLE prompt_settings (
    command TEXT PRIMARY KEY,
    description TEXT,
    system_prompt TEXT,
    llm_tag TEXT,
    updated_at TIMESTAMP DEFAULT NOW()
);
```

### Индексы

```sql
-- Для быстрого поиска сообщений
CREATE INDEX idx_messages_chat_date ON messages(chat_id, date_utc DESC);
CREATE INDEX idx_messages_user ON messages(from_user_id);

-- Для векторного поиска (IVFFlat)
CREATE INDEX idx_embeddings_vector ON message_embeddings
    USING ivfflat (embedding vector_cosine_ops);
```

---

## Технологии

| Компонент | Технология |
|-----------|------------|
| Runtime | .NET 9 |
| Web Framework | ASP.NET Core |
| Database | PostgreSQL 16 + pgvector |
| ORM | Dapper |
| Telegram | Telegram.Bot SDK |
| LLM | OpenRouter (Grok, Qwen, Perplexity, Llama) |
| Embeddings | OpenAI text-embedding-3-small |
| HTML Parsing | HtmlAgilityPack |

---

## Конфигурация

### appsettings.json

```json
{
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN",
    "AdminId": 123456789,
    "WebhookUrl": "https://your-domain.com/telegram/update",
    "SecretToken": "random-secret-for-webhook-validation"
  },

  "Database": {
    "ConnectionString": "Host=localhost;Database=watchmen;Username=...;Password=..."
  },

  "Embeddings": {
    "ApiKey": "sk-...",
    "Model": "text-embedding-3-small",
    "Dimensions": 1536
  },

  "Llm": {
    "DefaultProvider": "openrouter",
    "Providers": [
      {
        "Name": "grok",
        "Type": "openrouter",
        "Model": "x-ai/grok-4-fast",
        "Priority": 1,
        "Tags": ["default", "uncensored", "creative"],
        "Enabled": true
      },
      {
        "Name": "perplexity",
        "Model": "perplexity/sonar",
        "Priority": 3,
        "Tags": ["factcheck", "online"],
        "Enabled": true
      }
    ]
  }
}
```

### Переменные окружения

```bash
TELEGRAM__BOTTOKEN=your_bot_token
TELEGRAM__ADMINID=123456789
DATABASE__CONNECTIONSTRING=Host=...
EMBEDDINGS__APIKEY=sk-...
LLM__OPENROUTERKEY=sk-or-...
```

---

## Развёртывание

### Docker Compose

```yaml
version: '3.8'
services:
  bot:
    build: .
    environment:
      - TELEGRAM__BOTTOKEN=${BOT_TOKEN}
      - DATABASE__CONNECTIONSTRING=Host=db;Database=watchmen;...
    depends_on:
      - db
    ports:
      - "8080:8080"

  db:
    image: pgvector/pgvector:pg16
    environment:
      POSTGRES_DB: watchmen
      POSTGRES_PASSWORD: ${DB_PASSWORD}
    volumes:
      - pgdata:/var/lib/postgresql/data

volumes:
  pgdata:
```

### Требования

- PostgreSQL 16 с расширением pgvector
- HTTPS для webhook (или polling mode для разработки)
- API ключи: Telegram Bot, OpenRouter, OpenAI

### Webhook (Telegram)

- **HTTPS обязателен** — HTTP не поддерживается
- **Порты**: только 443, 80, 88, 8443
- **TLS 1.2+** — старые версии отклоняются
- **IP диапазоны Telegram**: `149.154.160.0/20` и `91.108.4.0/22`

### Управление webhook

```bash
# Установить webhook
curl -X POST "https://yourdomain.com/admin/set-webhook"

# Проверить статус
curl "https://yourdomain.com/admin/webhook-info"

# Удалить webhook
curl -X POST "https://yourdomain.com/admin/delete-webhook"
```

---

## Безопасность

- **Webhook validation** — проверка Secret Token в заголовке
- **IP filtering** — только запросы из диапазонов Telegram
- **Admin-only** — `/admin` доступен только указанному AdminId
- **No secrets in logs** — API ключи не логируются

---

## Уникальные особенности

1. **Многопровайдерная LLM архитектура** — автоматический fallback, разные провайдеры для разных задач
2. **RAG на embeddings** — ответы основаны на реальной истории чата
3. **Умное выделение тем** — структурированные выжимки вместо случайного сэмпла
4. **Импорт истории** — поддержка экспорта из Telegram Desktop
5. **Кастомные промпты** — администратор может менять поведение команд через `/admin prompt`
6. **Переименование пользователей** — обновляет историю и embeddings глобально

---

## Локальная разработка

### PostgreSQL с Docker

```bash
docker run --name watchmenbot-postgres \
  -e POSTGRES_PASSWORD=dev_password \
  -e POSTGRES_DB=watchmenbot_dev \
  -p 5432:5432 -d \
  pgvector/pgvector:pg16
```

### Запуск

```bash
cd WatchmenBot/WatchmenBot
dotnet run
```

Таблицы создаются автоматически при первом запуске (`DatabaseInitializer`).

---

## Лицензия

MIT
