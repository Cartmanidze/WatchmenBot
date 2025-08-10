using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Models;

namespace WatchmenBot.Services
{
    public class DailySummaryService : BackgroundService
    {
        private readonly ITelegramBotClient _bot;
        private readonly MessageStore _store;
        private readonly KimiClient _kimi;
        private readonly TimeSpan _runAtLocalTime; // e.g., 00:05

        public DailySummaryService(ITelegramBotClient bot, MessageStore store, KimiClient kimi)
        {
            _bot = bot;
            _store = store;
            _kimi = kimi;
            _runAtLocalTime = new TimeSpan(0, 5, 0);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.Now;
                var nextRunLocal = NextOccurrence(now, _runAtLocalTime);
                var delay = nextRunLocal - now;
                if (delay < TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                if (stoppingToken.IsCancellationRequested) break;

                await RunSummaryForYesterday(stoppingToken);
            }
        }

        private static DateTimeOffset NextOccurrence(DateTimeOffset now, TimeSpan atLocalTime)
        {
            var todayAt = new DateTimeOffset(now.Year, now.Month, now.Day, atLocalTime.Hours, atLocalTime.Minutes, atLocalTime.Seconds, now.Offset);
            var next = todayAt > now ? todayAt : todayAt.AddDays(1);
            return next;
        }

        private async Task RunSummaryForYesterday(CancellationToken ct)
        {
            var chatIds = await _store.GetDistinctChatIdsAsync();
            var nowLocal = DateTimeOffset.Now;
            var startLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset).AddDays(-1);
            var endLocal = startLocal.AddDays(1);
            var startUtc = startLocal.ToUniversalTime();
            var endUtc = endLocal.ToUniversalTime();

            foreach (var chatId in chatIds)
            {
                if (ct.IsCancellationRequested) break;
                var messages = await _store.GetMessagesAsync(chatId, startUtc, endUtc);
                if (messages.Count == 0) continue;

                var report = await BuildReportAsync(messages, ct);
                await _bot.SendTextMessageAsync(chatId: chatId, text: report, parseMode: ParseMode.Markdown, disableWebPagePreview: true, cancellationToken: ct);
            }
        }

        private async Task<string> BuildReportAsync(List<MessageRecord> messages, CancellationToken ct)
        {
            var total = messages.Count;
            var users = messages.GroupBy(m => m.FromUserId)
                .Select(g => new { UserId = g.Key, Name = g.Select(x => x.DisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? g.Select(x => x.Username).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)) ?? g.Key.ToString(), Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToList();

            var links = messages.Count(m => m.HasLinks);
            var media = messages.Count(m => m.HasMedia);

            var sample = messages.Count > 300 ? messages.Skip(Math.Max(0, messages.Count - 300)).ToList() : messages;
            var convo = new StringBuilder();
            foreach (var m in sample)
            {
                var name = string.IsNullOrWhiteSpace(m.DisplayName) ? (string.IsNullOrWhiteSpace(m.Username) ? m.FromUserId.ToString() : m.Username) : m.DisplayName;
                var text = string.IsNullOrWhiteSpace(m.Text) ? $"[{m.MessageType}]" : m.Text!.Replace("\n", " ");
                convo.AppendLine($"[{m.DateUtc.ToLocalTime():HH:mm}] {name}: {text}");
            }

            var statsText = new StringBuilder();
            statsText.AppendLine($"Всего сообщений: {total}");
            statsText.AppendLine($"Участников писали: {users.Count}");
            statsText.AppendLine($"Сообщений с ссылками: {links}");
            statsText.AppendLine($"Сообщений с медиа: {media}");
            statsText.AppendLine("Топ-10 активных:");
            foreach (var u in users.Take(10)) statsText.AppendLine($"- {u.Name}: {u.Count}");

            var system = "Ты — Kimi2, остроумный аналитик чата. Твоя задача: кратко и структурированно подвести итоги дня по переписке на русском языке. Будь дружелюбным и уместно ироничным, без токсичности. Формат ответа — Markdown с разделами: 1) Основное за день 2) Забавные наблюдения 3) Что решить/сделать 4) Цифры дня.";
            var userPrompt = new StringBuilder();
            userPrompt.AppendLine("Вот сводные метрики дня:");
            userPrompt.AppendLine(statsText.ToString());
            userPrompt.AppendLine();
            userPrompt.AppendLine("Фрагменты переписки (последние ~300 сообщений дня):");
            userPrompt.AppendLine("""" + convo.ToString() + """");
            userPrompt.AppendLine();
            userPrompt.AppendLine("Сформируй краткий отчёт. Не повторяй явные цифры из метрик, а используй их для контекста. В конце добавь 1-2 смелых, но безобидных шутки про активность чата.");

            var summary = await _kimi.CreateDailySummaryAsync(system, userPrompt.ToString(), ct);

            var header = "**Итоги дня**\n";
            var statsMd = "\n**Активность**\n" + statsText.ToString();
            return header + summary + statsMd;
        }
    }
} 