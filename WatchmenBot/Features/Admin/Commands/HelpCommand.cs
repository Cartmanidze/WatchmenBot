using Telegram.Bot;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin help - show admin command help
/// </summary>
public class HelpCommand(ITelegramBotClient bot, ILogger<HelpCommand> logger) : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        const string help = """
                            <b>🔧 Админ-команды</b>

                            <b>Просмотр:</b>
                            /admin status — текущие настройки
                            /admin report — отчёт по логам прямо сейчас
                            /admin chats — список известных чатов
                            /admin indexing — статус индексации эмбеддингов

                            <b>🔍 Debug:</b>
                            /admin debug — статус debug mode
                            /admin debug on — включить (отчёты в личку)
                            /admin debug off — выключить

                            <b>Импорт истории:</b>
                            /admin import &lt;chat_id&gt; — инструкция по импорту

                            <b>🤖 LLM:</b>
                            /admin llm — список провайдеров
                            /admin llm_set &lt;name&gt; — сменить дефолтный
                            /admin llm_on &lt;name&gt; — включить провайдера
                            /admin llm_off &lt;name&gt; — выключить провайдера
                            /admin llm_test — тест дефолтного
                            /admin llm_test &lt;name&gt; — тест конкретного

                            <b>🎭 Промпты:</b>
                            /admin prompts — список всех промптов
                            /admin prompt &lt;cmd&gt; — показать промпт
                            /admin prompt_tag &lt;cmd&gt; &lt;tag&gt; — установить LLM тег
                            /admin prompt_reset &lt;cmd&gt; — сбросить на дефолт

                            <b>👥 Имена (для исправления импорта):</b>
                            /admin names &lt;chat_id&gt; — список имён в чате
                            /admin rename &lt;chat_id&gt; "Старое" "Новое" — переименовать

                            <b>🔄 Переиндексация эмбеддингов:</b>
                            /admin reindex &lt;chat_id&gt; — инфо + подтверждение
                            /admin reindex all confirm — пересоздать ВСЕ

                            <b>📊 Контекстные эмбеддинги (окна 10 сообщений):</b>
                            /admin context — статистика по всем чатам
                            /admin context &lt;chat_id&gt; — детали чата
                            /admin context_reindex &lt;chat_id&gt; — инфо + подтверждение
                            /admin context_reindex all confirm — пересоздать ВСЕ

                            <b>Настройки:</b>
                            /admin set_summary_time HH:mm — время саммари
                            /admin set_report_time HH:mm — время отчёта
                            /admin set_timezone +N — часовой пояс
                            """;

        await SendMessageAsync(context.ChatId, help, ct);
        return true;
    }
}
