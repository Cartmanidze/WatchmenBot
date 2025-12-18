#!/bin/bash
# Production deployment script

set -e

echo "🚀 Deploying WatchmenBot to PRODUCTION..."

# Check .env exists
if [ ! -f .env ]; then
    echo "❌ No .env file found. Copy .env.production to .env and configure."
    exit 1
fi

# Check required variables
source .env
if [ -z "$TELEGRAM_WEBHOOK_URL" ]; then
    echo "❌ TELEGRAM_WEBHOOK_URL is not set in .env"
    exit 1
fi

if [ -z "$TELEGRAM_WEBHOOK_SECRET" ]; then
    echo "❌ TELEGRAM_WEBHOOK_SECRET is not set in .env"
    exit 1
fi

# Check SSL certificates
if [ ! -f nginx/ssl/fullchain.pem ]; then
    echo "⚠️  SSL certificates not found in nginx/ssl/"
    echo "   Run: ./scripts/setup-ssl.sh your-domain.com"
    exit 1
fi

echo "📦 Building containers..."
docker-compose -f docker-compose.yml -f docker-compose.prod.yml build --no-cache

echo "🔄 Restarting services..."
docker-compose -f docker-compose.yml -f docker-compose.prod.yml down
docker-compose -f docker-compose.yml -f docker-compose.prod.yml up -d

echo "⏳ Waiting for services to start..."
sleep 5

# Set webhook
echo "🔗 Setting Telegram webhook..."
curl -s "https://api.telegram.org/bot${TELEGRAM_BOT_TOKEN}/setWebhook?url=${TELEGRAM_WEBHOOK_URL}&secret_token=${TELEGRAM_WEBHOOK_SECRET}" | jq .

echo ""
echo "✅ Deployment complete!"
echo "📊 Logs: docker-compose -f docker-compose.yml -f docker-compose.prod.yml logs -f"
