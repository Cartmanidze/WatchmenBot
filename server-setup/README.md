# WatchmenBot — Быстрое развёртывание на сервере

## Требования

- VPS с Ubuntu 22.04+ (минимум 1 GB RAM)
- Домен или DuckDNS (бесплатно)
- Открытые порты 80 и 443

## Способ 1: Автоматическая установка

```bash
curl -fsSL https://raw.githubusercontent.com/Cartmanidze/WatchmenBot/main/server-setup/install.sh | sudo bash
```

Скрипт установит Docker и скачает все конфиги в `/opt/watchmenbot`.

---

## Способ 2: Ручная установка

### 1. Установка Docker

```bash
curl -fsSL https://get.docker.com | sh
systemctl enable docker && systemctl start docker
```

### 2. Создание директории

```bash
mkdir -p /opt/watchmenbot/nginx/ssl
cd /opt/watchmenbot
```

### 3. Скачивание конфигурации

```bash
REPO="https://raw.githubusercontent.com/Cartmanidze/WatchmenBot/main"

curl -fsSL "$REPO/docker-compose.server.yml" -o docker-compose.yml
curl -fsSL "$REPO/nginx/nginx.conf" -o nginx/nginx.conf
curl -fsSL "$REPO/.env.production" -o .env
```

---

## Настройка домена

### Вариант A: Свой домен

1. Добавь A-запись: `bot.example.com → IP_СЕРВЕРА`
2. Подожди 5-10 минут для распространения DNS

### Вариант B: DuckDNS (бесплатно)

1. Зарегистрируйся на https://www.duckdns.org
2. Создай поддомен (например: `mybot.duckdns.org`)
3. Укажи IP сервера

---

## SSL сертификат

```bash
# Установка certbot
apt update && apt install -y certbot

# Получение сертификата (порт 80 должен быть свободен!)
certbot certonly --standalone -d ТВОЙ_ДОМЕН

# Копирование сертификатов
cp /etc/letsencrypt/live/ТВОЙ_ДОМЕН/fullchain.pem /opt/watchmenbot/nginx/ssl/
cp /etc/letsencrypt/live/ТВОЙ_ДОМЕН/privkey.pem /opt/watchmenbot/nginx/ssl/
```

---

## Конфигурация

### Редактирование .env

```bash
nano /opt/watchmenbot/.env
```

Заполни все значения:

| Параметр | Описание | Как получить |
|----------|----------|--------------|
| `POSTGRES_PASSWORD` | Пароль БД | `openssl rand -base64 32` |
| `TELEGRAM_BOT_TOKEN` | Токен бота | @BotFather |
| `TELEGRAM_WEBHOOK_URL` | URL вебхука | `https://ТВОЙ_ДОМЕН/telegram/update` |
| `TELEGRAM_WEBHOOK_SECRET` | Секрет вебхука | `openssl rand -hex 32` |
| `OPENROUTER_API_KEY` | API ключ | https://openrouter.ai |
| `OPENAI_API_KEY` | API ключ | https://platform.openai.com |
| `ADMIN_USER_ID` | Твой Telegram ID | @userinfobot |
| `ADMIN_USERNAME` | Твой username | Без @ |

### Редактирование nginx.conf

```bash
nano /opt/watchmenbot/nginx/nginx.conf
```

Замени `your-domain.com` на свой домен в строках:
- `server_name your-domain.com;`
- `ssl_certificate` пути (если отличаются)

---

## Запуск

```bash
cd /opt/watchmenbot
docker compose up -d
```

### Проверка

```bash
# Статус контейнеров
docker compose ps

# Логи бота
docker compose logs -f watchmenbot

# Проверка webhook
curl -s "https://api.telegram.org/bot<ТОКЕН>/getWebhookInfo" | jq
```

---

## Обновление бота

```bash
cd /opt/watchmenbot
docker compose pull
docker compose up -d
```

---

## Автообновление SSL

Добавь в crontab:

```bash
crontab -e
```

```cron
0 0 1 * * certbot renew --quiet && cp /etc/letsencrypt/live/ТВОЙ_ДОМЕН/*.pem /opt/watchmenbot/nginx/ssl/ && docker compose -C /opt/watchmenbot restart nginx
```

---

## Решение проблем

### Webhook не работает

```bash
# Проверь SSL
openssl s_client -connect ТВОЙ_ДОМЕН:443 -servername ТВОЙ_ДОМЕН

# Проверь nginx
docker compose logs nginx

# Проверь что URL правильный
curl -I https://ТВОЙ_ДОМЕН/health
```

### Бот не отвечает

```bash
# Логи бота
docker compose logs watchmenbot

# Перезапуск
docker compose restart watchmenbot
```

### База данных

```bash
# Подключение к PostgreSQL
docker compose exec postgres psql -U postgres -d watchmenbot

# Проверка таблиц
\dt
```

---

## Полезные команды

| Команда | Описание |
|---------|----------|
| `docker compose ps` | Статус контейнеров |
| `docker compose logs -f` | Логи всех сервисов |
| `docker compose restart` | Перезапуск всех сервисов |
| `docker compose down` | Остановка |
| `docker compose up -d` | Запуск |
| `docker compose pull` | Обновление образов |

---

## Структура на сервере

```
/opt/watchmenbot/
├── docker-compose.yml    # Конфигурация Docker
├── .env                  # Переменные окружения
└── nginx/
    ├── nginx.conf        # Конфиг Nginx
    └── ssl/
        ├── fullchain.pem # SSL сертификат
        └── privkey.pem   # Приватный ключ
```
