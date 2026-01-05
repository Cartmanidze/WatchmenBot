# WatchmenBot

**Telegram-бот для анализа группового общения с AI-памятью, векторным поиском и персональными профилями.**

WatchmenBot сохраняет историю сообщений, создаёт семантические embeddings, строит профили пользователей и отвечает на вопросы с учётом контекста и личности каждого участника.

---

## Возможности

### Команды для пользователей

| Команда | Описание |
|---------|----------|
| `/summary [N]` | Выжимка чата за N часов (по умолчанию 24). Форматы: `48`, `48h`, `2d` |
| `/ask <вопрос>` | Вопрос по истории чата с памятью о пользователе (RAG + профили) |
| `/smart <вопрос>` | Поиск в интернете (Perplexity, без контекста чата) |
| `/truth [N]` | Fact-check последних N сообщений (по умолчанию 5) |

### Автоматические функции

- **Ежедневная выжимка** — отправляется в настроенное время (по умолчанию 21:00)
- **Фоновая индексация** — все сообщения автоматически получают embeddings
- **Профилирование** — извлечение фактов о пользователях каждые 15 минут
- **Ночные профили** — глубокий анализ пользователей в 03:00 UTC
- **Отчёт администратору** — ежедневная статистика использования

### Команды администратора

Полный набор через `/admin`:

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

### Обзор системы

```
┌─────────────────────────────────────────────────────────────────┐
│                        Telegram Bot API                         │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                      ASP.NET Core WebAPI                         │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │              ProcessTelegramUpdate                           │ │
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
│  │  ┌───────────┐ ┌───────────┐ ┌───────────┐ ┌─────────────┐   │ │
│  │  │ LlmRouter │ │ Embedding │ │  Message  │ │   Profile   │   │ │
│  │  │           │ │  Service  │ │   Store   │ │   System    │   │ │
│  │  └─────┬─────┘ └─────┬─────┘ └─────┬─────┘ └──────┬──────┘   │ │
│  └────────┼─────────────┼─────────────┼──────────────┼─────────┘ │
└───────────┼─────────────┼─────────────┼──────────────┼───────────┘
            │             │             │              │
            ▼             ▼             ▼              ▼
┌───────────────┐ ┌───────────────┐ ┌───────────────────────────────┐
│   OpenRouter  │ │  HuggingFace  │ │         PostgreSQL            │
│  (LLM API)    │ │  (Embeddings) │ │    + pgvector extension       │
└───────────────┘ └───────────────┘ └───────────────────────────────┘
```

### Структура проекта

```
WatchmenBot/
├── Features/                       # Вертикальные слайсы
│   ├── Admin/                      # Админские команды
│   ├── Messages/                   # Обработка сообщений
│   ├── Search/                     # RAG поиск (/ask, /truth)
│   ├── Summary/                    # Дневные саммари
│   └── Webhook/                    # Telegram webhook
│
├── Infrastructure/                 # Инфраструктурный слой
│   └── Database/                   # БД, миграции
│
├── Services/                       # Общие сервисы
│   ├── Llm/                        # LLM провайдеры
│   ├── ProfileQueueService         # Очередь на профилирование
│   ├── ProfileWorkerService        # Извлечение фактов (каждые 15 мин)
│   ├── ProfileGeneratorService     # Глубокие профили (03:00 UTC)
│   ├── LlmMemoryService            # Память о пользователях
│   ├── RagFusionService            # Multi-query RAG с RRF
│   ├── RerankService               # LLM-ранжирование результатов
│   ├── EmbeddingService            # Векторный поиск
│   └── SmartSummaryService         # Умные выжимки
│
└── Extensions/                     # Extension методы
```

---

## Система профилей пользователей

### Трёхуровневая архитектура

```
┌─────────────────────────────────────────────────────────────────┐
│  РЕАЛТАЙМ: При каждом сообщении                                 │
│  • Обновляем счётчики (message_count, last_message_at)          │
│  • Добавляем сообщение в очередь на анализ                      │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  ФОНОВЫЙ ВОРКЕР: Каждые 15 минут                                │
│  • Берёт сообщения из очереди (батчами по 50)                   │
│  • Группирует по пользователю                                   │
│  • Извлекает факты через LLM                                    │
│  • Сохраняет в user_facts                                       │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  НОЧНОЙ ПЕРЕСЧЁТ: Раз в сутки (03:00 UTC)                       │
│  • Для каждого активного пользователя:                          │
│    - Собирает все факты из user_facts                           │
│    - Сэмплирует 40 сообщений                                    │
│    - Генерирует глубокий профиль через LLM                      │
│    - Обновляет user_profiles                                    │
└─────────────────────────────────────────────────────────────────┘
```

### Типы фактов

| Тип | Описание | Пример |
|-----|----------|--------|
| `likes` | Что нравится | "любит футбол" |
| `dislikes` | Что не нравится | "терпеть не может понедельники" |
| `said` | Прямые высказывания | "сказал что переезжает" |
| `does` | Действия, привычки | "использует ник Gun Done" |
| `knows` | Экспертиза | "разбирается в криптовалютах" |
| `opinion` | Мнения | "считает что Python лучше" |

### Глубокий профиль

Генерируется раз в сутки и включает:
- **summary** — краткое описание человека
- **communication_style** — стиль общения
- **role_in_chat** — роль (активист/наблюдатель/эксперт/тролль)
- **interests** — массив интересов
- **traits** — черты характера
- **roast_material** — темы для добрых подколов

---

## Алгоритмы команд

### `/ask` — Вопрос с памятью

```
┌─────────────────────────────────────────────────────────────────┐
│  1. ЗАГРУЗКА ПАМЯТИ                                             │
│     BuildEnhancedContextAsync()                                 │
│     → Профиль + релевантные факты из user_facts                 │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  2. RAG FUSION (если не персональный вопрос)                    │
│     • Генерация 3 вариаций запроса                              │
│     • Параллельный поиск по каждой                              │
│     • Объединение через RRF (Reciprocal Rank Fusion)            │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  3. RERANK                                                      │
│     LLM-ранжирование результатов по релевантности               │
│     Фильтрация нерелевантных (score < 1)                        │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  4. КОНТЕКСТНЫЕ ОКНА                                            │
│     Для топ-10 результатов загружаются ±1 соседних сообщения    │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  5. ДВУХЭТАПНАЯ ГЕНЕРАЦИЯ                                       │
│                                                                 │
│     Stage 1: ФАКТЫ (temp=0.1)                                   │
│     → JSON с извлечёнными фактами, цитатами, roast_target       │
│                                                                 │
│     Stage 2: ОТВЕТ (temp=0.6)                                   │
│     → Два источника: ПАМЯТЬ + КОНТЕКСТ ЧАТА                     │
│     → Выбирает релевантный источник для ответа                  │
│     → Дерзкий стиль с подколами                                 │
└─────────────────────────────────────────────────────────────────┘
```

**Ключевая особенность**: Если в памяти есть прямой ответ (например, "использует ник Gun Done"), он будет использован даже если RAG нашёл другую информацию.

### `/summary` — Умная выжимка

```
┌─────────────────────────────────────────────────────────────────┐
│  1. РАЗНООБРАЗНАЯ ВЫБОРКА                                       │
│     GetDiverseMessagesAsync() → 100 разных сообщений            │
│     (максимально охватывает все темы периода)                   │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  2. ИЗВЛЕЧЕНИЕ ТЕМ                                              │
│     LLM (temp=0.3) → JSON: ["тема1", "тема2", ...]              │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  3. ПОИСК КОНТЕКСТА ПО ТЕМАМ                                    │
│     Для каждой темы: векторный поиск релевантных сообщений      │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  4. ДВУХЭТАПНАЯ ГЕНЕРАЦИЯ                                       │
│     Stage 1: Факты (temp=0.3)                                   │
│     Stage 2: Юмор (temp=0.6)                                    │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  РЕЗУЛЬТАТ:                                                     │
│  🔥 Главное → 😂 Лучшие моменты → 💬 Темы → 🏆 Герои → 🎭 Итог  │
└─────────────────────────────────────────────────────────────────┘
```

### `/smart` — Поиск в интернете

Прямой запрос к Perplexity без использования контекста чата.

### `/truth` — Fact-check

Проверка последних N сообщений через Perplexity с поиском в интернете.

---

## Сводная таблица команд

| Команда | Embeddings | LLM | Память | Этапов |
|---------|:----------:|:---:|:------:|:------:|
| `/summary` | поиск тем | Grok | ❌ | 2 |
| `/ask` | RAG Fusion + Rerank | Grok | ✅ | 2 |
| `/smart` | ❌ | Perplexity | ❌ | 1 |
| `/truth` | ❌ | Perplexity | ❌ | 1 |

---

## База данных

### Основные таблицы

```sql
-- Сообщения
CREATE TABLE messages (
    chat_id BIGINT NOT NULL,
    id BIGINT NOT NULL,
    date_utc TIMESTAMP NOT NULL,
    from_user_id BIGINT,
    from_user_name TEXT,
    text TEXT,
    PRIMARY KEY (chat_id, id)
);

-- Векторные embeddings (pgvector, 1024 dims для HuggingFace)
CREATE TABLE message_embeddings (
    id SERIAL PRIMARY KEY,
    chat_id BIGINT NOT NULL,
    message_ids BIGINT[] NOT NULL,
    embedding vector(1024) NOT NULL,
    text TEXT NOT NULL,
    metadata JSONB,
    created_at TIMESTAMP DEFAULT NOW()
);

-- Очередь на профилирование
CREATE TABLE message_queue (
    id SERIAL PRIMARY KEY,
    chat_id BIGINT NOT NULL,
    message_id BIGINT NOT NULL,
    user_id BIGINT NOT NULL,
    display_name TEXT,
    text TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    processed BOOLEAN DEFAULT FALSE,
    UNIQUE(chat_id, message_id)
);

-- Факты о пользователях
CREATE TABLE user_facts (
    id SERIAL PRIMARY KEY,
    user_id BIGINT NOT NULL,
    chat_id BIGINT NOT NULL,
    fact_type TEXT NOT NULL,
    fact_text TEXT NOT NULL,
    confidence FLOAT DEFAULT 0.7,
    source_message_ids BIGINT[],
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(chat_id, user_id, fact_text)
);

-- Профили пользователей
CREATE TABLE user_profiles (
    chat_id BIGINT NOT NULL,
    user_id BIGINT NOT NULL,
    display_name TEXT,
    message_count INT DEFAULT 0,
    last_message_at TIMESTAMPTZ,
    active_hours JSONB DEFAULT '{}',
    summary TEXT,
    communication_style TEXT,
    role_in_chat TEXT,
    interests JSONB DEFAULT '[]',
    traits JSONB DEFAULT '[]',
    roast_material JSONB DEFAULT '[]',
    profile_version INT DEFAULT 1,
    last_profile_update TIMESTAMPTZ,
    PRIMARY KEY (chat_id, user_id)
);
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
| LLM | OpenRouter (Grok, Qwen, Perplexity) |
| Embeddings | HuggingFace (1024 dims) |

---

## Конфигурация

### appsettings.json

```json
{
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN",
    "AdminId": 123456789
  },
  "Database": {
    "ConnectionString": "Host=localhost;Database=watchmen;..."
  },
  "Embeddings": {
    "Provider": "huggingface",
    "ApiKey": "hf_...",
    "Dimensions": 1024
  },
  "Llm": {
    "Providers": [
      {
        "Name": "grok",
        "Type": "openrouter",
        "Model": "x-ai/grok-4-fast",
        "Priority": 1,
        "Tags": ["default", "uncensored"]
      },
      {
        "Name": "perplexity",
        "Model": "perplexity/sonar",
        "Tags": ["factcheck"]
      }
    ]
  },
  "ProfileService": {
    "QueueProcessingIntervalMinutes": 15,
    "MinMessagesForFactExtraction": 3,
    "NightlyProfileTime": "03:00",
    "MinMessagesForProfile": 10
  }
}
```

---

## Развёртывание

### Docker Compose

```yaml
services:
  bot:
    build: .
    environment:
      - TELEGRAM__BOTTOKEN=${BOT_TOKEN}
      - DATABASE__CONNECTIONSTRING=Host=db;Database=watchmen;...
    depends_on:
      - db

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
- API ключи: Telegram Bot, OpenRouter, HuggingFace

---

## Лицензия

MIT
