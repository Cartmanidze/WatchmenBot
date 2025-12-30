# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2025-12-30]

### Added
- **Background Summary Processing** — фоновая генерация выжимок:
  - `SummaryQueueService` — in-memory очередь запросов на summary (Channel-based)
  - `BackgroundSummaryWorker` — фоновый воркер для обработки очереди
  - Команда `/summary` теперь сразу отвечает "Генерирую выжимку..." и запускает генерацию в фоне
  - Решает проблему nginx timeout 60 сек — summary может занимать 30-120 сек

### Changed
- **Disabled Rerank in /ask** — отключён LLM-переранжирование результатов:
  - Экономия 5-7 секунд и ~1300 токенов на запрос
  - RAG Fusion + RRF достаточно для хорошей сортировки
  - LLM на Stage 1/2 сам выбирает релевантное из контекста
- **Nginx timeout increased** — увеличен `proxy_read_timeout` с 60s до 180s

## [2025-12-29]

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
- **RAG Fusion with participant context** — улучшены вариации запросов:
  - Передаются имена участников чата в LLM для генерации вариаций
  - LLM видит список участников и не путает имена с техническими терминами
  - Пример: "бек" с участником "Бек" → вариации для имени, а не "backend"
- **RAG Fusion text patterns** — LLM теперь генерирует текстовые паттерны чата:
  - "смеется" → ахахах, хахаха, лол, )))
  - "да" → ага, угу, +, ок
  - Эмбеддинги семантических синонимов могут быть далеки от реальных текстовых паттернов
- **Reranker: reorder only, no filtering** — Reranker больше не фильтрует результаты:
  - Раньше: score=0 → результат удалялся (терялись "ХАХАХА" как нерелевантные)
  - Теперь: reranker только переупорядочивает, доверяем RAG Fusion + ILIKE для поиска
- **AskHandler Stage 2 prompt** — переработан промпт для правильного использования памяти:
  - Память и RAG теперь представлены как равноправные источники
  - Правило: "Если в памяти есть прямой ответ — используй его"
  - Убрана путаница между JSON-фактами и memory context

### Fixed
- **Memory facts ignored bug** — исправлена проблема когда LLM игнорировал факты из памяти в пользу RAG контекста
- **Query self-match bug** — фильтрация результатов с similarity >= 0.98 (почти точные совпадения с запросом)

---

## [2025-12-28]

### Changed
- **HuggingFace Embeddings** — переход с OpenAI на HuggingFace:
  - Новый провайдер `EmbeddingProvider.HuggingFace`
  - Обновлена схема БД для 1024-мерных векторов
  - Динамический выбор провайдера через конфигурацию

### Fixed
- **HuggingFace API URL** — исправлен URL с deprecated api-inference на router.huggingface.co

---

## [2025-12-27]

### Changed
- **LlmMemoryService refactoring** — рефакторинг для лучшей совместимости с Dapper:
  - Заменены internal records на classes для nullable DateTimeOffset
  - Улучшена логика парсинга дат

### Fixed
- **Dapper mapping bug** — исправлен маппинг колонок с явными алиасами:
  - `chat_id AS ChatId`, `user_id AS UserId`
  - Решена проблема ChatId=0 и DateUtc=MinValue

---

## [2025-12-26]

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
