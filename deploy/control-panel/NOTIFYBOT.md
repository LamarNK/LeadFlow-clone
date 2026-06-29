# NotifyBot на сервере Orbita

NotifyBot разворачивается **на том же VPS**, что и Orbita (`163.5.153.207`), в каталоге `/opt/orbita`.

## Схема

| Компонент | Порт (localhost) | Публичный URL |
|-----------|------------------|---------------|
| Orbita Web | 8081 | https://orbitsu.ru |
| Orbita API | 8082 | https://api.orbitsu.ru |
| **NotifyBot** | **8083** | **не нужен** (polling) |
| Orbita PostgreSQL | 5432 | только localhost |
| NotifyBot PostgreSQL | 5433 | только localhost |

Бот работает в **polling-режиме**: сам опрашивает Telegram, публичный домен не требуется.
3DS-коды запрашиваются по команде `/sms`, `/check` или тегом бота.
Plusofon — через API по ключу доступа (webhook не используется).

## Первый деплой

1. Скопируйте на сервер актуальные файлы из `deploy/control-panel/` в `/opt/orbita/`.

2. Добавьте переменные NotifyBot в `/opt/orbita/.env` (см. `.env.example`):

```bash
NOTIFYBOT_DB_PASSWORD=<strong-password>
TELEGRAM_BOT_TOKEN=<bot-token>
PLUSOFON_API_TOKEN=<ключ доступа из ЛК: Разработчикам → Доступ к API>
PLUSOFON_SMS_FETCH_LIMIT=20
```

Привязка карт к чатам — **через Telegram-бота**, не через `.env`.

3. Соберите Docker-образ на сервере (из корня репозитория):

```bash
./deploy/control-panel/build-notifybot-image.sh notifybot-api:prod
```

4. Поднимите сервисы:

```bash
cd /opt/orbita
docker compose -f docker-compose.images.yml --env-file .env up -d notifybot-postgres notifybot-api
```

5. Проверьте бота: в чате офиса отправьте `/sms` — бот заберёт свежие SMS из Plusofon и вернёт 3DS-код.

## Настройка маршрутизации (@andreypakin, @LamarrNK)

1. Напишите боту `/start` в личку.
2. Добавьте бота в группы офисов.
3. В панели бота: «Чаты» — убедитесь, что чаты видны.
4. «Карты» → привязать карту к чату.
5. `/sms` в чате офиса — проверить получение кода.

## Обновление версии

```bash
cd /path/to/LeadFlow
./deploy/control-panel/build-notifybot-image.sh notifybot-api:prod
cd /opt/orbita
docker compose -f docker-compose.images.yml --env-file .env up -d notifybot-api
```

## Бэкап БД

Скрипт `backup-db.sh` бэкапит и Orbita, и NotifyBot:

```bash
/opt/orbita/backup-db.sh
```

Файлы: `/opt/orbita/backups/notifybot_YYYYMMDD_HHMMSS.sql.gz`

## Plusofon

В `.env` укажите `PLUSOFON_API_TOKEN` (ключ доступа из ЛК → Разработчикам → Доступ к API). Заголовок `Client` всегда `10553`.

Схема работы: оплатили → в чате офиса `/sms` или тегнули бота → получили код.
Если к чату привязаны карты, бот покажет коды только для них.