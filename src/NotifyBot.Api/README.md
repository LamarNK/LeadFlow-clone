# NotifyBot

Telegram-сервис для пересылки 3DS SMS с виртуального номера Plusofon в чаты офисов.

## Состав

- `NotifyBot.Api` — HTTP webhook endpoint
- `NotifyBot.Application` — бизнес-логика маршрутизации
- `NotifyBot.Domain` — сущности и модели
- `NotifyBot.Infrastructure` — EF Core, Telegram, парсер SMS

## Требования

- .NET 10 SDK
- Docker (для PostgreSQL)
- Telegram-бот (админы: `@andreypakin`, `@LamarrNK`)

## Быстрый старт (Docker)

1. Скопируйте переменные окружения:

```bash
cp deploy/notify-bot/.env.example deploy/notify-bot/.env
```

2. Заполните `.env` реальными значениями Telegram и Plusofon.

3. Запустите стек:

```bash
docker compose -f deploy/notify-bot/docker-compose.yml up --build
```

API будет доступен на `http://localhost:8090`.

## Локальный запуск без Docker-образа API

1. Поднимите только PostgreSQL:

```bash
docker compose -f deploy/notify-bot/docker-compose.yml up postgres -d
```

2. Настройте `src/NotifyBot.Api/appsettings.Development.json` (или user secrets).

3. Запустите API:

```bash
dotnet run --project src/NotifyBot.Api
```

API стартует на `http://localhost:5090`.

## Настройка Plusofon

В `.env` или `appsettings` укажите API-доступ:

```text
PLUSOFON_API_TOKEN=<ключ доступа из ЛК>
```

Заголовок `Client` для Plusofon API всегда `10553` (зашито в коде, в `.env` не нужен).

Мгновенный webhook Plusofon — платная опция; используем запрос по команде в Telegram.

## Получение 3DS-кода

В чате офиса (или личке):

```text
/sms
/check
@YourBotName
```

Бот запросит свежие входящие SMS через `GET api/v1/sms` и вернёт распознанный 3DS-код.
Если к чату привязаны карты — покажет коды только для них.

## Настройка маршрутизации (админы)

Доступ только у `@andreypakin` и `@LamarrNK`. Chat ID больше не задаются в `.env` — всё через бота.

1. Напишите боту `/start` в личку (регистрация админа).
2. Добавьте бота в нужные группы **или** используйте личные сообщения как получатель.
3. `/chats` — список групп и ЛС, куда добавлен бот.
4. В целевом чате (группа или ЛС): `/bind 1062` — привязать карту к этому чату.
5. Альтернатива: `/bind 1062 -100123456789` — привязать к chat ID из `/chats`.
6. `/cards` — проверить привязки.

Команды:

| Команда | Описание |
|---------|----------|
| `/chats` | Группы и личные чаты бота |
| `/cards` | Карты и их получатели |
| `/bind 1062` | Привязать карту к текущему чату |
| `/bind 1062 -100…` | Привязать к указанному chat ID |
| `/addcard 1234 Office4` | Добавить карту |
| `/enable 1062` / `/disable 1062` | Вкл/выкл карту |

Webhook для команд бота (настраивается автоматически при старте):

```text
https://<your-host>/api/webhooks/telegram
```

## Карты по умолчанию (seed)

| Last4 | Label   |
|-------|---------|
| 1062  | Office3 |
| 9669  | Office1 |
| 3098  | Office2 |

Получатель (`DestinationChatId`) задаётся через `/bind`.

## Тесты

```bash
dotnet test NotifyBot.Tests/NotifyBot.Tests.csproj
```

## Production (сервер Orbita)

NotifyBot разворачивается на том же VPS, что и Orbita (`/opt/orbita`).

Инструкция: [deploy/control-panel/NOTIFYBOT.md](../../../deploy/control-panel/NOTIFYBOT.md)

Команды бота: `/sms`, `/check` (см. [NOTIFYBOT.md](../../../deploy/control-panel/NOTIFYBOT.md)).