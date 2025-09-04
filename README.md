# WatchmenBot

A Telegram bot for daily chat summaries in groups. Collects messages throughout the day, analyzes participant activity, and generates funny/ironic reports using Kimi2 (OpenAI-compatible API).

## Features
- Read messages from groups/supergroups via **webhooks** (HTTPS)
- Secure validation of Telegram requests (secret token + IP filtering)
- Message storage in **PostgreSQL** using **Dapper**
- Daily summary at 00:05 local time for the previous day
- Brief insights, funny observations, and activity statistics

## Requirements
- .NET 9 SDK
- **PostgreSQL** server
- Telegram bot token
- Kimi2 API key (e.g., OpenRouter). Default uses `moonshotai/kimi-k2` model and `https://openrouter.ai/api` base URL
- **Public HTTPS domain** for webhook (ports 443, 80, 88, 8443)

## Installation and Setup
1. Clone the repository and open the project folder:
   ```bash
   cd WatchmenBot/WatchmenBot
   ```
2. Set up PostgreSQL and create database:
   ```sql
   CREATE DATABASE watchmenbot;
   ```
3. Configure settings in `appsettings.json`:
   - `Database:ConnectionString` — PostgreSQL connection string
   - `Telegram:BotToken` — token from @BotFather
   - `Telegram:WebhookUrl` — your public HTTPS URL (e.g., `https://yourdomain.com/telegram/update`)
   - `Telegram:WebhookSecret` — random string 1-256 chars (A-Z, a-z, 0-9, _, -)
   - `Kimi:ApiKey` — API key
   - optionally `Kimi:BaseUrl`, `Kimi:Model`
4. In BotFather disable Group Privacy (so bot can see group messages).
5. Add bot to a test group.
6. **Deploy to HTTPS server** (Azure, AWS, VPS with SSL/TLS).
7. Run the bot — it will automatically create PostgreSQL tables.
8. Set webhook via admin endpoint:
   ```bash
   curl -X POST "https://yourdomain.com/admin/set-webhook"
   ```

**Endpoints:**
- `/` — health check (`WatchmenBot is running`)
- `/health` — health check with PostgreSQL validation
- `/telegram/update` — receive updates from Telegram (POST)
- `/admin/set-webhook` — set webhook (POST)
- `/admin/delete-webhook` — delete webhook (POST)
- `/admin/webhook-info` — webhook information (GET)

## Quick Testing
- Write 10-20 messages in the group (preferably different types: text, links, images).
- To avoid waiting until night, temporarily change the report time in `DailySummaryService` (default 00:05) to the next few minutes and restart.
- Or wait until 00:05 — the report will come for "yesterday".

## Configuration
File `WatchmenBot/appsettings.json`:
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

## Architecture

### Core
- `Program.cs` — minimal startup (13 lines)
- `Models/MessageRecord.cs` — message model

### Services
- `Services/MessageStore.cs` — PostgreSQL storage with Dapper
- `Services/DailySummaryService.cs` — daily summary generation
- `Services/KimiClient.cs` — Kimi2 client (OpenAI-compatible Chat Completions)

### Features (Clean Architecture)
- `Features/Webhook/ProcessTelegramUpdate.cs` — webhook processing orchestrator
- `Features/Messages/SaveMessage.cs` — message persistence logic
- `Features/Admin/SetWebhook.cs` — webhook setup
- `Features/Admin/DeleteWebhook.cs` — webhook deletion  
- `Features/Admin/GetWebhookInfo.cs` — webhook information

### Infrastructure
- `Controllers/TelegramWebhookController.cs` — thin webhook controller
- `Controllers/TelegramAdminController.cs` — thin admin controller
- `Extensions/ServiceCollectionExtensions.cs` — DI configuration
- `Extensions/WebApplicationExtensions.cs` — application configuration
- `Extensions/TelegramSecurityExtensions.cs` — security validation
- `Extensions/TelegramUpdateParserExtensions.cs` — update parsing
- `Infrastructure/Database/` — PostgreSQL connection and initialization

## Deployment and Security

### Webhook Requirements (Telegram)
- **HTTPS mandatory** — HTTP not supported
- **Ports**: only 443, 80, 88, 8443
- **TLS 1.2+** — older SSL/TLS versions rejected
- **Telegram IP ranges**: `149.154.160.0/20` and `91.108.4.0/22`

### Secret Setup
Generate random secret (1-256 characters):
```bash
# PowerShell
-join ((65..90) + (97..122) + (48..57) + 95 + 45 | Get-Random -Count 32 | % {[char]$_})

# Linux/Mac
openssl rand -base64 32 | tr -d "=+/" | cut -c1-32
```

### Webhook Management
After deployment, use admin endpoints:

**Set webhook:**
```bash
curl -X POST "https://yourdomain.com/admin/set-webhook"
```

**Check status:**
```bash
curl "https://yourdomain.com/admin/webhook-info"
```

**Delete webhook:**
```bash
curl -X POST "https://yourdomain.com/admin/delete-webhook"
```

### Debugging
- Logs available via `ILogger<Program>`
- Unauthorized requests logged as Warning
- Processing errors logged as Error

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

## Notes
- `appsettings.Development.json` is ignored by Git and can contain local secrets.
- Tables are created automatically on first startup (`DatabaseInitializer`).
- Uses composite PRIMARY KEY (`chat_id`, `id`) for message uniqueness.
- Default uses OpenRouter (`moonshotai/kimi-k2`). You can specify your own Kimi endpoint if needed. 