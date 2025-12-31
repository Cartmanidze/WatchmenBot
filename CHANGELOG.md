# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2025-12-31]

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
- **EmbeddingService Refactoring** — разделение God Object на специализированные сервисы:
  - `EmbeddingService` сокращён с 1715 до 943 строк — на 45% меньше кода
  - Теперь делегирует вызовы специализированным сервисам (backward compatible)
  - Новые специализированные сервисы:
    - `EmbeddingStorageService` — хранение и управление эмбеддингами в БД
    - `PersonalSearchService` — персональные запросы пользователей (GetUserMessagesAsync, GetMentionsOfUserAsync, GetPersonalContextAsync)
    - `ContextWindowService` — работа с контекстными окнами (GetContextWindowAsync, GetMergedContextWindowsAsync)
  - Публичный API `EmbeddingService` остался без изменений — все методы делегируются
  - Улучшенная поддержка SRP (Single Responsibility Principle)
  - Упрощение тестирования и поддержки кода

- **Database Query Optimization** — устранение N+1 проблем в запросах к БД:
  - **ContextWindowService.GetMergedContextWindowsAsync**: **30 запросов → 1 запрос** (30x быстрее!)
    - Использует `LATERAL JOIN` вместо цикла с повторяющимися запросами
    - Для 10 сообщений: было 30 DB roundtrips, стало 1 запрос
    - Применяется в `/ask` при расширении результатов контекстными окнами
  - **PersonalSearchService.GetPersonalContextAsync**: **4 запроса → 1 запрос** (4x быстрее!)
    - Использует `UNION` для объединения сообщений пользователя и упоминаний
    - Вместо цикла по именам (username + displayName) с 2 запросами на каждое
    - Применяется в личных запросах типа "когда Я говорил..." или "@username когда..."
    - Использует `ANY()` оператор для множественных имён в одном запросе
  - Значительно снижает latency при поиске с множественными результатами

- **Database Index Optimization** — добавлены недостающие индексы для ускорения запросов:
  - **idx_message_embeddings_metadata_gin** — GIN индекс на JSONB поле `metadata`:
    - Ускоряет JSON-запросы в PersonalSearchService (поиск по Username/DisplayName)
    - Использует `jsonb_path_ops` для оптимальной производительности
  - **idx_message_embeddings_text_search** — GIN индекс для полнотекстового поиска:
    - Полнотекстовый поиск по `chunk_text` с поддержкой русского языка
    - Использует `to_tsvector('russian', chunk_text)` для морфологического анализа
  - **idx_messages_user_date** — композитный индекс `(chat_id, from_user_id, date_utc DESC)`:
    - Оптимизирует запросы поиска сообщений конкретного пользователя в чате
    - Применяется в PersonalSearchService при фильтрации по автору
  - Все индексы создаются автоматически в `DatabaseInitializer.CreateIndexesAsync()`

- **AdminCommandHandler Command Pattern Refactoring** — рефакторинг админских команд с использованием Command Pattern:
  - `AdminCommandHandler` сокращён с 1504 до 119 строк (**сокращение на 92%**, -1385 строк!)
  - Создана инфраструктура Command Pattern:
    - `IAdminCommand` — интерфейс для команд
    - `AdminCommandBase` — базовый класс с общими зависимостями и утилитами
    - `AdminCommandRegistry` — реестр команд для маршрутизации
    - `AdminCommandContext` — контекст выполнения команды
  - **ВСЕ команды** извлечены в отдельные классы (24 команды):
    - **Monitoring** (5): `StatusCommand`, `ReportCommand`, `ChatsCommand`, `IndexingCommand`, `HelpCommand`
    - **Debug** (1): `DebugCommand` — режим отладки (`/admin debug on/off`, `/admin debug`)
    - **Settings** (3): `SetSummaryTimeCommand`, `SetReportTimeCommand`, `SetTimezoneCommand`
    - **LLM Management** (4): `LlmListCommand` (список), `LlmTestCommand` (тест), `LlmSetCommand` (установка default), `LlmToggleCommand` (вкл/выкл)
    - **Import** (1): `ImportCommand` — импорт истории чата из Telegram export (с поддержкой file upload)
    - **Prompt Management** (4): `PromptsCommand` (список), `PromptCommand` (просмотр/обновление), `PromptResetCommand`, `PromptTagCommand`
    - **User Management** (2): `NamesCommand` (список имён), `RenameCommand` (переименование)
    - **Embedding Management** (4): `ReindexCommand` (переиндексация message embeddings), `ContextCommand` (статистика context embeddings), `ContextReindexCommand` (переиндексация context embeddings)
  - `AdminCommandHandler` теперь — чистый роутер (119 строк):
    - Проверка доступа администратора
    - Делегирование через `AdminCommandRegistry`
    - Обработка ошибок
    - Всё остальное — в отдельных командах
  - Улучшена тестируемость: каждая команда изолирована и тестируется независимо
  - Упрощено добавление новых админ-команд: просто создать класс и зарегистрировать
  - DI регистрация в `ServiceCollectionExtensions.cs`:
    - Стандартная регистрация через `registry.Register<T>(name)`
    - Кастомная factory для `LlmToggleCommand` (разные bool параметры для llm_on/llm_off)
  - Убраны неиспользуемые зависимости из конструктора `AdminCommandHandler`

- **AskHandler Service Extraction Refactoring** — двухэтапный рефакторинг обработчика `/ask` и `/smart` команд:
  - **Этап 1**: `AskHandler` сокращён с 893 до 440 строк (**-51%**, -453 строки)
    - Извлечена бизнес-логика в специализированные сервисы:
      - **`SearchStrategyService`** (~230 строк) — стратегии поиска (personal hybrid + context-only)
      - **`ContextBuilderService`** (~120 строк) — построение контекста с token budget (4000 tokens)
      - **`AnswerGeneratorService`** (~160 строк) — LLM генерация с debug support
  - **Этап 2**: `AskHandler` сокращён с 440 до 272 строк (**-38%**, -168 строк)
    - Дальнейшее извлечение вспомогательной логики:
      - **`AskHandlerHelpers`** (static) — утилиты: ParseQuestion, GetDisplayName, ParseTimestamp, EstimateTokens
      - **`PersonalQuestionDetector`** — определение персональных вопросов ("я", "@username")
      - **`DebugReportCollector`** — сбор debug информации для отчётов
      - **`ConfidenceGateService`** — проверка confidence + early returns для None/Low
  - **Итого**: `AskHandler` сокращён с **893 до 272 строк** (**сокращение на 70%**, -621 строка!)
  - `AskHandler` теперь — минималистичный оркестратор (272 строки):
    - Валидация входных данных
    - Параллельное выполнение memory loading + search
    - Делегирование всей логики в специализированные сервисы
    - Финальная отправка ответа пользователю
  - Улучшена тестируемость: каждый компонент полностью изолирован
  - Упрощена поддержка: изменения в любой части логики не затрагивают другие компоненты
  - Сохранена вся функциональность: параллелизм, debug reporting, personal detection, HTML sanitization

- **Profile System Pipeline Refactoring** — рефакторинг системы профилей с использованием Pipeline/Orchestrator паттерна:
  - `ProfileWorkerService` сокращён с 241 до 71 строк — теперь только главный цикл и делегирование
  - `ProfileGeneratorService` сокращён с 326 до 78 строк — теперь только scheduling и делегирование
  - Новая архитектура с разделением ответственности:
    - `IProfileHandler` — общий интерфейс для обработчиков профилей
    - `ProfileOrchestrator` — координация обоих pipeline stages
    - `FactExtractionHandler` — извлечение фактов из очереди сообщений
    - `ProfileGenerationHandler` — ночная генерация глубоких профилей
  - `ProfileMetrics` на базе `System.Diagnostics.Metrics` — thread-safe метрики
  - `ProfileOptions` — централизованная конфигурация
  - Улучшенная тестируемость: все компоненты изолированы
  - Упрощённое добавление новых этапов обработки профилей
  - Метрики по handler'ам и типам ошибок для диагностики
  - Адаптивная обработка очереди: продолжает если есть больше работы

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
