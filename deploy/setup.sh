#!/usr/bin/env bash
# One-shot installer for Ubuntu Server 22.04/24.04. Run from the project root:  bash deploy/setup.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP=/opt/eprescription
IP="$(hostname -I | awk '{print $1}')"

sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0 nginx openssl curl ca-certificates
if ! command -v node >/dev/null || [ "$(node -v | tr -d v | cut -d. -f1)" -lt 18 ]; then
  curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
  sudo apt-get install -y nodejs
fi

echo "== Building frontend =="
(cd "$ROOT/frontend" && npm install --no-audit --no-fund && npm run build)
echo "== Publishing backend =="
dotnet publish "$ROOT/backend" -c Release -o /tmp/ep-publish

id -u eprescription >/dev/null 2>&1 || sudo useradd --system --no-create-home --shell /usr/sbin/nologin eprescription
sudo mkdir -p $APP/api $APP/web $APP/data /etc/eprescription
sudo rm -rf $APP/api/* $APP/web/*
sudo cp -r /tmp/ep-publish/. $APP/api/
sudo cp -r "$ROOT/frontend/dist/." $APP/web/
sudo chown -R eprescription:eprescription $APP/data

if [ ! -f /etc/eprescription/env ]; then
  ADMIN_PW="$(openssl rand -base64 18 | tr -d '/+=' | cut -c1-16)Aa1!"
  sudo tee /etc/eprescription/env >/dev/null <<ENV
ASPNETCORE_URLS=http://127.0.0.1:5000
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__Default=Data Source=$APP/data/eprescription.db
Jwt__Secret=$(openssl rand -hex 32)
KeyMaster=$(openssl rand -hex 32)
Admin__Email=admin@eprescription.local
Admin__Password=$ADMIN_PW
Seed__Demo=false
ENV
  sudo chmod 600 /etc/eprescription/env
  echo "== ADMIN LOGIN: admin@eprescription.local / $ADMIN_PW  (also stored in /etc/eprescription/env) =="
fi

if [ ! -f /etc/ssl/certs/eprescription.crt ]; then
  sudo openssl req -x509 -nodes -days 365 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 \
    -subj "/CN=eprescription.local" -addext "subjectAltName=DNS:eprescription.local,IP:$IP" \
    -keyout /etc/ssl/private/eprescription.key -out /etc/ssl/certs/eprescription.crt
fi

sudo cp "$ROOT/deploy/eprescription.service" /etc/systemd/system/
sudo cp "$ROOT/deploy/nginx.conf" /etc/nginx/sites-available/eprescription
sudo ln -sf /etc/nginx/sites-available/eprescription /etc/nginx/sites-enabled/eprescription
sudo rm -f /etc/nginx/sites-enabled/default
sudo systemctl daemon-reload
sudo systemctl enable --now eprescription
sudo nginx -t && sudo systemctl restart nginx
echo "== Done. Open https://$IP  (accept the self-signed certificate warning) =="
