# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- **/admin indexing Command** — новая команда для мониторинга статуса индексации:
  - Показывает прогресс индексации для каждого типа эмбеддингов (message/context)
  - Визуальные progress bar для каждого handler'а
  - Общая статистика: Total, Indexed, Pending items
  - Интеграция с новой Pipeline/Orchestrator архитектурой
  - Полезно для проверки состояния фоновой индексации без SSH доступа

- **Full Context Reindex Command** — команда полной реиндексации контекстных эмбеддингов:
  - `/admin context_reindex all` — показать предупреждение о полной реиндексации
  - `/admin context_reindex all confirm` — удалить ВСЕ контекстные эмбеддинги из всех чатов
  - Новый метод `ContextEmbeddingService.DeleteAllContextEmbeddingsAsync()` — полная очистка таблицы
  - BackgroundService автоматически начнёт переиндексацию с нуля для всех чатов
  - Полезно при изменении логики создания окон или после багфиксов

- **Hybrid Embedding Search** — гибридный поиск с использованием обоих типов эмбеддингов:
  - **Персональные запросы** ("я гондон?", "@username"):
    - Параллельный поиск в `message_embeddings` (точные сообщения пользователя) + `context_embeddings` (полные диалоги)
    - Fallback на `context_embeddings` если не найдены точные сообщения пользователя
    - Расширение точных совпадений контекстными окнами для полноты диалога
  - **Обычные запросы** ("о чём спорили?"):
    - Параллельный поиск в `context_embeddings` (приоритет) + `message_embeddings` (точные совпадения)
    - Дедупликация по message_id с сохранением лучшего similarity
    - Context windows получают 1.0x similarity, отдельные сообщения 0.85x
  - **Summary (/summary)**:
    - Для каждой темы параллельный поиск: `message_embeddings` (разнообразие) + `context_embeddings` (полные диалоги)
    - Объединение ~15 отдельных сообщений + ~5 контекстных окон per тема
    - Более связные и детальные саммари с полным контекстом диалогов
  - Новый метод `ContextEmbeddingService.GetContextWindowsByMessageIdsAsync()` — поиск окон по списку message_id
  - Новый метод `AskHandler.SearchPersonalWithHybridAsync()` — гибридный поиск для персональных запросов
  - Модифицирован `AskHandler.SearchContextOnlyAsync()` — теперь использует оба типа эмбеддингов
  - Модифицирован `SmartSummaryService` — инъекция `ContextEmbeddingService` и гибридный поиск по темам

### Changed
- **Embedding Indexing Pipeline Refactoring** — рефакторинг фоновой системы индексации с использованием Pipeline/Orchestrator паттерна:
  - `BackgroundEmbeddingService` сокращён с 215 до 84 строк — теперь только главный цикл и делегирование
  - Новая архитектура с разделением ответственности:
    - `IEmbeddingHandler` — общий интерфейс для обработчиков (message/context embeddings)
    - `EmbeddingOrchestrator` — координация pipeline из нескольких handlers
    - `BatchProcessor` — управление батчами + rate limiting + error tracking
    - `MessageEmbeddingHandler` — индексация отдельных сообщений
    - `ContextEmbeddingHandler` — индексация контекстных окон (sliding windows)
  - `IndexingMetrics` на базе `System.Diagnostics.Metrics` — thread-safe метрики с Counter/Histogram
  - `IndexingOptions` — централизованная конфигурация для всех компонентов
  - Улучшенная тестируемость: все компоненты изолированы и легко мокаются
  - Упрощённое добавление новых типов эмбеддингов: достаточно реализовать `IEmbeddingHandler`
  - Автоматическая обработка HTTP 429 (TooManyRequests) с retry delay
  - Метрики по handler'ам и типам ошибок для диагностики

- **Improved Context Quality** — улучшение качества контекста:
  - Персональные вопросы получают как точные цитаты, так и полный контекст диалогов
  - Обычные вопросы комбинируют широкий контекст (окна) с точными совпадениями (сообщения)
  - Summary теперь показывает не только ключевые сообщения, но и полные обсуждения по темам

- **Optimized Context Building** — оптимизация построения контекста в `/ask`:
  - `BuildContextWithWindowsAsync` теперь переиспользует `context_text` напрямую из `context_embeddings`
  - Убрано дублирование: больше не вызывает `GetMergedContextWindowsAsync` для результатов, уже содержащих полные окна
  - Новый флаг `SearchResult.IsContextWindow` — отмечает результаты, уже содержащие форматированный контекст
  - Экономия 2-3 SQL запросов в messages таблицу на каждый `/ask` запрос
  - Результаты из обоих источников (`context_embeddings` + `message_embeddings`) теперь обрабатываются эффективно

### Technical
- Все поисковые запросы выполняются параллельно через `Task.WhenAll` — минимальная задержка
- Нет дополнительного использования памяти — используются существующие таблицы `message_embeddings` и `context_embeddings`
- Оба типа эмбеддингов теперь необходимы для оптимальной работы поиска

## [2025-12-30]

### Changed
- **Context-Only Search** — упрощён поиск, убран RAG Fusion:
  - Используются только `context_embeddings` (окна по 10 сообщений)
  - Убран `RagFusionService` из `AskHandler` — экономия 3-4 сек на LLM вариациях
  - Новый метод `SearchContextOnlyAsync` — прямой поиск по контекстным окнам
  - Удалён `MergeSearchResultsAsync` — не нужен без гибридного поиска
- **One-Stage LLM Generation** — объединение двух LLM-вызовов в один:
  - Был Two-Stage: Stage 1 (факты T=0.1) + Stage 2 (юмор T=0.6) = 2 вызова
  - Теперь One-Stage: один вызов с T=0.5 — экономия ~1-2 сек
  - Удалён метод `IsFactsEmpty` — больше не нужен
- **Dynamic Dialog Windows** — динамические окна по границам диалогов:
  - Определение границ по временным промежуткам (>30 мин = новый диалог)
  - Маленькие диалоги (5-15 сообщений) → одно окно целиком
  - Большие диалоги → скользящие окна внутри диалога
  - Новый метод `SegmentIntoDialogs()` для сегментации
  - Убраны фиксированные константы, окна адаптируются под диалоги

### Added
- **Context-Aware Embeddings (Sliding Windows)** — контекстные эмбеддинги:
  - Новая таблица `context_embeddings` — хранит эмбеддинги для окон из 10 сообщений
  - `ContextEmbeddingService` — создание и поиск по контекстным окнам
  - Скользящие окна: размер 10 сообщений, шаг 3 (перекрытие 7 сообщений)
  - Сохраняет контекст диалога: "Да, согласен" теперь имеет смысл вместе с предыдущими сообщениями
  - Гибридный поиск в `/ask`: RAG Fusion + Context Embeddings параллельно
  - Результаты контекстного поиска получают буст +0.1 к similarity

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
- **Removed ILIKE fallback** — убран текстовый поиск из RAG Fusion:
  - ILIKE добавлял 1-2 секунды (4 последовательных запроса)
  - Фиксированный score 0.95 ломал ранжирование
  - Порог 3 символа пропускал нужный сленг ("лол", "хах")
  - RAG Fusion вариации уже включают текстовые паттерны
- **Parallel Memory + Search + ParticipantNames** — максимальная параллелизация в `/ask`:
  - Memory context, participant names и RAG Fusion запускаются одновременно
  - Экономия ~0.5 секунды на запрос
  - `/smart` больше не загружает memory (не использовался)
  - Удалён неиспользуемый `RerankService` из DI
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
