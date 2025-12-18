#!/bin/bash
# Development mode - polling

set -e

echo "🚀 Starting WatchmenBot in DEVELOPMENT mode (polling)..."

# Use .env.development if .env doesn't exist
if [ ! -f .env ]; then
    if [ -f .env.development ]; then
        cp .env.development .env
        echo "📄 Copied .env.development to .env"
    else
        echo "❌ No .env file found. Copy .env.development to .env and configure."
        exit 1
    fi
fi

docker-compose -f docker-compose.yml -f docker-compose.dev.yml down
docker-compose -f docker-compose.yml -f docker-compose.dev.yml build
docker-compose -f docker-compose.yml -f docker-compose.dev.yml up -d

echo "✅ Bot started in polling mode"
echo "📊 Logs: docker-compose logs -f watchmenbot"
