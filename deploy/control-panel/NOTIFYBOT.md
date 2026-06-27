# NotifyBot на сервере Orbita

NotifyBot разворачивается **на том же VPS**, что и Orbita (`163.5.153.207`), в каталоге `/opt/orbita`.

## Схема

| Компонент | Порт (localhost) | Публичный URL |
|-----------|------------------|---------------|
| Orbita Web | 8081 | https://orbitsu.ru |
| Orbita API | 8082 | https://api.orbitsu.ru |
| **NotifyBot API** | **8083** | **https://notify.orbitsu.ru** |
| Orbita PostgreSQL | 5432 | только localhost |
| NotifyBot PostgreSQL | 5433 | только localhost |

Webhook для Plusofon:

```text
https://notify.orbitsu.ru/api/webhooks/plusofon
```

## DNS

Добавьте A-запись:

```text
notify.orbitsu.ru  →  163.5.153.207
```

## Первый деплой

1. Скопируйте на сервер актуальные файлы из `deploy/control-panel/` в `/opt/orbita/`.

2. Добавьте переменные NotifyBot в `/opt/orbita/.env` (см. `.env.example`):

```bash
NOTIFYBOT_DB_PASSWORD=<strong-password>
TELEGRAM_BOT_TOKEN=<bot-token>
PLUSOFON_SECRET=<plusofon-secret>
PLUSOFON_WEBHOOK_VALIDATION=true
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

5. Обновите Caddy и перезагрузите:

```bash
sudo cp /opt/orbita/Caddyfile /etc/caddy/Caddyfile
sudo systemctl reload caddy
```

6. Проверьте webhook:

```bash
curl -X POST "https://notify.orbitsu.ru/api/webhooks/plusofon?secret=<PLUSOFON_SECRET>" \
  -H "Content-Type: application/json" \
  -d '{"text":"Для оплаты в ticket.rzd.ru 6,194.60 RUB Карта *1062; 3DS код: 645755"}'
```

Ожидается `200 OK`. Сообщение уйдёт в чат, куда привязана карта `1062` (см. ниже).

## Настройка маршрутизации (@andreypakin, @LamarrNK)

1. Напишите боту `/start` в личку.
2. Добавьте бота в группы офисов (или привязывайте карты к личным сообщениям).
3. `/chats` — убедитесь, что чаты видны.
4. В нужном чате: `/bind 1062`, `/bind 9669`, `/bind 3098`.
5. `/cards` — проверить привязки.

Команды: `/help`, `/chats`, `/cards`, `/bind`, `/addcard`, `/enable`, `/disable`.

Webhook бота (регистрируется при старте API):

```text
https://notify.orbitsu.ru/api/webhooks/telegram
```

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

В личном кабинете Plusofon укажите URL webhook:

```text
https://notify.orbitsu.ru/api/webhooks/plusofon
```

Если включена проверка секрета, передавайте `PLUSOFON_SECRET` через:

- заголовок `X-Plusofon-Secret`, или
- query `?secret=...`