# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- **Hybrid Profile System** — новая система профилей пользователей:
  - `ProfileQueueService` — очередь сообщений для фонового анализа
  - `ProfileWorkerService` — воркер для извлечения фактов каждые 15 минут
  - `ProfileGeneratorService` — ночная генерация глубоких профилей (03:00 UTC)
  - Таблица `message_queue` — очередь на анализ
  - Таблица `user_facts` — отдельные факты о пользователях
  - Расширение `user_profiles` — summary, communication_style, role_in_chat, roast_material
- **Enhanced Memory Context** — улучшенный контекст памяти в `/ask`:
  - `BuildEnhancedContextAsync()` — использует user_facts для ответов
  - Два равноправных источника: память + RAG контекст
  - LLM выбирает релевантный источник для ответа

### Changed
- **AskHandler Stage 2 prompt** — переработан промпт для правильного использования памяти:
  - Память и RAG теперь представлены как равноправные источники
  - Правило: "Если в памяти есть прямой ответ — используй его"
  - Убрана путаница между JSON-фактами и memory context

### Fixed
- **Memory facts ignored bug** — исправлена проблема когда LLM игнорировал факты из памяти в пользу RAG контекста

---

## [2024-12-28]

### Changed
- **HuggingFace Embeddings** — переход с OpenAI на HuggingFace:
  - Новый провайдер `EmbeddingProvider.HuggingFace`
  - Обновлена схема БД для 1024-мерных векторов
  - Динамический выбор провайдера через конфигурацию

### Fixed
- **HuggingFace API URL** — исправлен URL с deprecated api-inference на router.huggingface.co

---

## [2024-12-27]

### Changed
- **LlmMemoryService refactoring** — рефакторинг для лучшей совместимости с Dapper:
  - Заменены internal records на classes для nullable DateTimeOffset
  - Улучшена логика парсинга дат

### Fixed
- **Dapper mapping bug** — исправлен маппинг колонок с явными алиасами:
  - `chat_id AS ChatId`, `user_id AS UserId`
  - Решена проблема ChatId=0 и DateUtc=MinValue

---

## [2024-12-26]

### Changed
- **AskHandler prompt refinement** — уточнены правила использования фактов:
  - Ужесточены языковые гайдлайны
  - Абстрагированы технические детали

### Fixed
- **Minimum text length filter** — снижен порог с 10 до 5 символов для хранения эмбеддингов

---

## [Earlier]

### Added
- RAG Fusion search с RRF (Reciprocal Rank Fusion)
- Rerank Service — LLM-based переранжирование результатов
- Two-stage generation — Stage 1: факты (T=0.1), Stage 2: юмор (T=0.6)
- Daily Summary Service — автоматические дневные саммари
- Background Embedding Service — фоновая генерация эмбеддингов
- Chat Import — импорт истории из Telegram HTML export
- Admin Commands — /admin stats, /admin logs, /admin chats
- Debug Service — детальные отчёты для отладки
