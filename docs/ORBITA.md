# Орбита — панель мониторинга воркеров

**Орбита** — центральная веб-панель для мониторинга распределённых экземпляров LeadFlow на VDS.

## Состав

- `Orbita.Api` — REST API + PostgreSQL
- `Orbita.Web` — MVC (Controllers + Views + ViewModels)
- `Orbita.Contracts` — общие DTO
- `tools/OrbitaMockWorker` — имитация воркеров для тестов

## Локальный запуск

1. Поднять PostgreSQL:

```bash
docker compose -f deploy/control-panel/docker-compose.yml up postgres -d
```

2. Запустить API:

```bash
dotnet run --project Orbita.Api
```

3. Запустить панель:

```bash
dotnet run --project Orbita.Web
```

4. (Опционально) Запустить mock-воркеры:

```bash
dotnet run --project tools/OrbitaMockWorker
```

## Вход в панель

- URL: `https://localhost:7123` (вход: `/Account/Login`)
- Email: `admin@orbita.local`
- Пароль: `OrbitaAdmin1!` (из `Orbita.Api/appsettings.json`)

## LibMan (локальные библиотеки)

```bash
cd Orbita.Web
libman restore
```

Font Awesome 4.7: `wwwroot/lib/font-awesome/`

## Docker (полный стек)

```bash
docker compose -f deploy/control-panel/docker-compose.yml up --build
```

- Панель: http://localhost:8081
- API: http://localhost:8080

## API для воркеров (будущая интеграция)

- `POST /api/v1/workers/register`
- `POST /api/v1/workers/heartbeat`
- `POST /api/v1/workers/telemetry/snapshot`
- `POST /api/v1/workers/telemetry/events`

Авторизация воркера: `Authorization: Bearer {apiKey}`.

## Развёртывание на VPS (production)

Подробно: `deploy/ORBITA_SERVER_DEPLOY.md`

Кратко:
- DNS: `orbitsu.ru`, `www.orbitsu.ru`, `api.orbitsu.ru` → A-запись на IP сервера.
- В панели хостинга открой порты 22, 80, 443, 8080.
- SSH только по ключу, CrowdSec + Caddy (HTTPS) на сервере.
- Публичный ключ:
  `ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAfD+Y6/za2llrCsfESqBUl5r+osc+sZZ7LzhH0z12JO orbita`
- Готовые секреты для `.env`:
  ```
  REGISTRATION_SECRET=vfqFzRXuK5sPJdL3oNEr1MeYWZwxCDaUG62n0V8m
  JWT_KEY=GwxfgyOpT72RkYEeVdcqQbu8AvlZ3IDFP19HWzLMj6srt4KB
  ADMIN_PASSWORD=Orbq0Mei6RpAku7tIvd!
  CORS_ORIGIN_0=https://orbitsu.ru
  CORS_ORIGIN_1=https://www.orbitsu.ru
  ```
- На сервере в `/opt/orbita`:
  `docker compose --env-file .env up -d`
- Caddy: `deploy/control-panel/Caddyfile` → `/etc/caddy/Caddyfile`
- Бэкап БД: `/opt/orbita/backup-db.sh` (cron ежедневно в 03:00)

Панель: https://orbitsu.ru/
API: https://api.orbitsu.ru/
NotifyBot (3DS SMS → Telegram): https://notify.orbitsu.ru/
Fallback (пока DNS): http://163.5.153.207/

## NotifyBot (тот же VPS)

Telegram-бот для пересылки 3DS SMS с Plusofon. Разворачивается рядом с Orbita в `/opt/orbita`.

Подробно: `deploy/control-panel/NOTIFYBOT.md`

Кратко:
- DNS: `notify.orbitsu.ru` → A-запись на IP сервера
- Webhook Plusofon: `https://notify.orbitsu.ru/api/webhooks/plusofon`
- Docker-сервисы: `notifybot-postgres` (порт 5433), `notifybot-api` (порт 8083)
- Caddy проксирует `notify.orbitsu.ru` → `127.0.0.1:8083`
- Переменные в `.env`: `TELEGRAM_*`, `PLUSOFON_SECRET` (см. `.env.example`)