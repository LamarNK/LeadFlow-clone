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