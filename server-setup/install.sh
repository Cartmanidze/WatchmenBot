#!/bin/bash
# WatchmenBot - Server Installation Script
# Usage: curl -fsSL https://raw.githubusercontent.com/GITHUB_USER/WatchmenBot/main/server-setup/install.sh | bash

set -e

echo "🤖 WatchmenBot Installation"
echo "==========================="

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

# Check root
if [ "$EUID" -ne 0 ]; then
    echo -e "${RED}Запусти скрипт от root: sudo bash install.sh${NC}"
    exit 1
fi

# Install Docker if not exists
if ! command -v docker &> /dev/null; then
    echo -e "${YELLOW}📦 Установка Docker...${NC}"
    curl -fsSL https://get.docker.com | sh
    systemctl enable docker
    systemctl start docker
fi

# Install docker-compose plugin if not exists
if ! docker compose version &> /dev/null; then
    echo -e "${YELLOW}📦 Установка Docker Compose...${NC}"
    apt-get update
    apt-get install -y docker-compose-plugin
fi

# Create directory
echo -e "${YELLOW}📁 Создание директории...${NC}"
mkdir -p /opt/watchmenbot/nginx/ssl
cd /opt/watchmenbot

# Download files
echo -e "${YELLOW}📥 Загрузка конфигурации...${NC}"
REPO_URL="https://raw.githubusercontent.com/Cartmanidze/WatchmenBot/main"

curl -fsSL "$REPO_URL/docker-compose.server.yml" -o docker-compose.yml
curl -fsSL "$REPO_URL/nginx/nginx.conf" -o nginx/nginx.conf
curl -fsSL "$REPO_URL/.env.production" -o .env

echo ""
echo -e "${GREEN}✅ Базовая установка завершена!${NC}"
echo ""
echo -e "${YELLOW}Следующие шаги:${NC}"
echo ""
echo "1. Настрой домен (A-запись → $(curl -s ifconfig.me))"
echo ""
echo "2. Получи SSL сертификат:"
echo "   certbot certonly --standalone -d ТВОЙ_ДОМЕН"
echo "   cp /etc/letsencrypt/live/ТВОЙ_ДОМЕН/fullchain.pem /opt/watchmenbot/nginx/ssl/"
echo "   cp /etc/letsencrypt/live/ТВОЙ_ДОМЕН/privkey.pem /opt/watchmenbot/nginx/ssl/"
echo ""
echo "3. Отредактируй конфигурацию:"
echo "   nano /opt/watchmenbot/.env"
echo "   nano /opt/watchmenbot/nginx/nginx.conf"
echo ""
echo "4. Запусти:"
echo "   cd /opt/watchmenbot && docker compose up -d"
echo ""
