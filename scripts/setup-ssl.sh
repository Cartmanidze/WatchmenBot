#!/bin/bash
# Setup SSL certificates using certbot

set -e

DOMAIN=$1

if [ -z "$DOMAIN" ]; then
    echo "Usage: ./setup-ssl.sh your-domain.com"
    exit 1
fi

echo "🔐 Setting up SSL for $DOMAIN..."

# Create directories
mkdir -p nginx/ssl
mkdir -p nginx/certbot

# Update nginx config with domain
sed -i "s/your-domain.com/$DOMAIN/g" nginx/nginx.conf

# Start nginx for certbot challenge (HTTP only)
echo "📦 Starting nginx for certificate challenge..."
docker run -d --name certbot-nginx \
    -p 80:80 \
    -v $(pwd)/nginx/certbot:/var/www/certbot \
    nginx:alpine

# Get certificate
echo "📜 Obtaining certificate..."
docker run --rm \
    -v $(pwd)/nginx/ssl:/etc/letsencrypt \
    -v $(pwd)/nginx/certbot:/var/www/certbot \
    certbot/certbot certonly \
    --webroot \
    --webroot-path=/var/www/certbot \
    --email admin@$DOMAIN \
    --agree-tos \
    --no-eff-email \
    -d $DOMAIN

# Copy certificates
cp nginx/ssl/live/$DOMAIN/fullchain.pem nginx/ssl/
cp nginx/ssl/live/$DOMAIN/privkey.pem nginx/ssl/

# Stop temporary nginx
docker stop certbot-nginx
docker rm certbot-nginx

echo "✅ SSL certificates installed!"
echo "   - nginx/ssl/fullchain.pem"
echo "   - nginx/ssl/privkey.pem"
