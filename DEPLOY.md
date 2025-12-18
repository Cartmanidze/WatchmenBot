# WatchmenBot — Руководство по развёртыванию

## Окружения

### Development (Локальная разработка)
- Режим **polling** (webhooks не нужны)
- PostgreSQL на порту 5433
- Бот доступен на http://localhost:8080

### Production (Сервер)
- Режим **webhooks** с SSL
- Nginx reverse proxy
- PostgreSQL не доступен извне

---

## Локальная разработка

### 1. Настройка окружения
```bash
cp .env.development .env
# Отредактируй .env — укажи свои токены
```

### 2. Запуск бота
```bash
# Через скрипт
./scripts/dev.sh

# Или вручную
docker-compose -f docker-compose.yml -f docker-compose.dev.yml up -d
```

### 3. Просмотр логов
```bash
docker-compose logs -f watchmenbot
```

---

## Развёртывание на сервере

### Требования
- VPS с Docker и Docker Compose
- Домен, направленный на IP сервера
- Открытые порты 80 и 443

### 1. Клонирование репозитория
```bash
git clone <repo> /opt/watchmenbot
cd /opt/watchmenbot
```

### 2. Настройка окружения
```bash
cp .env.production .env
nano .env
```

Обязательные параметры:
```env
POSTGRES_PASSWORD=<надёжный-пароль>
TELEGRAM_BOT_TOKEN=<токен-бота>
TELEGRAM_WEBHOOK_URL=https://твой-домен.com/telegram/update
TELEGRAM_WEBHOOK_SECRET=<случайные-64-символа>
OPENROUTER_API_KEY=<ключ-openrouter>
OPENAI_API_KEY=<ключ-openai>
ADMIN_USER_ID=<твой-telegram-id>
ADMIN_USERNAME=<твой-username>
```

Генерация webhook secret:
```bash
openssl rand -hex 32
```

### 3. Настройка SSL сертификатов
```bash
chmod +x scripts/*.sh
./scripts/setup-ssl.sh твой-домен.com
```

Или вручную через certbot:
```bash
apt install certbot
certbot certonly --standalone -d твой-домен.com
cp /etc/letsencrypt/live/твой-домен.com/fullchain.pem nginx/ssl/
cp /etc/letsencrypt/live/твой-домен.com/privkey.pem nginx/ssl/
```

### 4. Обновление конфига nginx
```bash
nano nginx/nginx.conf
# Замени "your-domain.com" на свой домен
```

### 5. Деплой
```bash
./scripts/deploy.sh
```

### 6. Проверка
```bash
# Проверка контейнеров
docker-compose -f docker-compose.yml -f docker-compose.prod.yml ps

# Просмотр логов
docker-compose -f docker-compose.yml -f docker-compose.prod.yml logs -f

# Проверка webhook
curl https://api.telegram.org/bot<ТОКЕН>/getWebhookInfo
```

---

## Полезные команды

| Команда | Описание |
|---------|----------|
| `docker-compose logs -f watchmenbot` | Логи бота |
| `docker-compose exec postgres psql -U postgres -d watchmenbot` | Доступ к БД |
| `docker-compose restart watchmenbot` | Перезапуск бота |
| `docker-compose down && docker-compose up -d` | Полный перезапуск |

---

## Решение проблем

### Webhook не работает
1. Проверь что SSL сертификат валидный
2. Проверь что TELEGRAM_WEBHOOK_URL совпадает с доменом
3. Смотри логи nginx: `docker-compose logs nginx`

### Бот не отвечает
1. Смотри логи бота: `docker-compose logs watchmenbot`
2. Проверь правильность TELEGRAM_BOT_TOKEN
3. Проверь подключение к БД

### Эмбеддинги не создаются
1. Проверь что OPENAI_API_KEY установлен
2. Смотри логи на предмет ошибок API
3. Проверь что таблица embeddings существует

---

## Обновление SSL сертификатов

Сертификаты истекают каждые 90 дней. Настрой автообновление:

```bash
# Добавь в crontab
0 0 1 * * cd /opt/watchmenbot && ./scripts/renew-ssl.sh
```

Создай `scripts/renew-ssl.sh`:
```bash
#!/bin/bash
certbot renew --quiet
cp /etc/letsencrypt/live/твой-домен.com/*.pem nginx/ssl/
docker-compose -f docker-compose.yml -f docker-compose.prod.yml restart nginx
```

---

## Структура проекта

```
watchmenbot/
├── .env                      # Конфигурация (не в git)
├── .env.development          # Шаблон для разработки
├── .env.production           # Шаблон для продакшена
├── docker-compose.yml        # Базовая конфигурация
├── docker-compose.dev.yml    # Оверлей для разработки
├── docker-compose.prod.yml   # Оверлей для продакшена
├── nginx/
│   ├── nginx.conf            # Конфиг nginx
│   └── ssl/                  # SSL сертификаты
├── scripts/
│   ├── dev.sh                # Запуск в dev режиме
│   ├── deploy.sh             # Деплой на сервер
│   └── setup-ssl.sh          # Настройка SSL
└── WatchmenBot/              # Исходный код
```
