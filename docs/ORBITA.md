# Орбита — панель мониторинга воркеров

**Орбита** — центральная веб-панель для мониторинга распределённых экземпляров LeadFlow на VDS.

## Состав

- `Orbita.Api` — REST API + PostgreSQL
- `Orbita.Web` — MVC (Controllers + Views + ViewModels)
- `Orbita.Contracts` — общие DTO
- `Orbita.TelephonyGateway` — опциональный изолированный ingress и зашифрованная очередь событий телефонии
- `asterisk` (Compose profile `telephony-media`) — опциональная отдельная SIP/RTP-звонилка и загрузка записей в приватное хранилище CRM
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

## Воркер (Orbita.Worker)

Фоновое приложение Windows (только иконка в трее). Парсит Avito через AdsPower, Multilogin X или обычный установленный Chrome/Chromium и отправляет отклики в Орбиту. Bitrix — на стороне портала.

Воркер поддерживает три независимых runtime браузера:

| Источник | Как появляется в панели | Как запускается |
| --- | --- | --- |
| AdsPower | синхронизация Local API | `browser/start` + CDP |
| Multilogin X | синхронизация каталога профилей | launcher + CDP |
| Обычный браузер | создание по названию в панели (или подключение существующей папки) | `Puppeteer.LaunchAsync` с отдельным `User Data` |

Для обычного браузера:

- в настройках воркера можно указать путь к `chrome.exe` (необязательно; иначе ищется в Program Files);
- у каждого аккаунта своя папка профиля на диске воркера (`%LocalAppData%\Orbita\ChromeProfiles\<account-guid>` по умолчанию) — стандартный профиль Chrome пользователя и `Default` использовать нельзя;
- в строке аккаунта есть ⚙ **Настройки профиля** (компактная боковая панель, только для Local Chrome): логин/пароль Avito, HTTP-прокси `host:port` и кнопка «Открыть браузер»;
- пароли Avito и прокси хранятся через ASP.NET Data Protection и не возвращаются в панель, HTML, логи, telemetry и SignalR — только флаги «заданы»; пустой пароль при сохранении оставляет текущий, «Очистить данные» снимает учётку Avito;
- прокси на этом этапе только HTTP/HTTPS (`--proxy-server=http://host:port` + `AuthenticateAsync` на вкладке). Схема и `user:password@` в адресе запрещены; SOCKS не поддерживается (отдельная задача);
- если прокси включён, тот же адрес уходит в RuCaptcha/GeeTest, чтобы IP браузера и капчи совпадали;
- первый вход в Avito делается отдельной командой «Открыть браузер» (`https://www.avito.ru/`) и не запускает мониторинг;
- ручная сессия и мониторинг одного аккаунта взаимоисключающие (`LocalChromeAccountLock`); статусы браузера: «Свободен», «Открыт вручную», «Мониторится»;
- cookies и сессия Avito живут в папке профиля; воркер создаёт её при первом запуске и никогда не удаляет;
- синхронизация AdsPower/Multilogin не затирает и не удаляет local-аккаунты.

В настройках воркера у каждого источника есть переключатель «Использовать AdsPower / Multilogin / обычный браузер» (по умолчанию все включены). Выключение провайдера **не удаляет** аккаунты и настройки: каталог этого источника не синхронизируется, его аккаунты не занимают слоты параллелизма и не запускаются. В таблице они остаются со статусом «Провайдер выключен».

На вкладке **Подключение** три компактные карточки (AdsPower, Multilogin X, обычный браузер) со статусом: «Выключен», «Требуется настройка», «Не проверено», «Проверяется», «Подключён», «Ошибка подключения». Статус «Подключён» ставит только успешная проверка **на воркере** — панель сама соединение не проверяет и успех не подставляет. Поля провайдера видны только когда он включён; скрытие и выключение не затирают сохранённые URL, ключи и пути.

«Проверить подключение» ставит задачу воркеру (AdsPower Local API — профили и группы, Multilogin — token + launcher, обычный Chrome — автопоиск или указанный `chrome.exe`). После успеха у AdsPower/Multilogin появляется «Синхронизировать сейчас» — немедленная синхронизация каталога на воркере.

### Установка (MSI)

1. В панели: **Воркеры → Добавить воркер** → сохраните API-ключ (показывается один раз).
2. На VDS запустите `Orbita.Worker.Setup-{version}.msi` из `publish/out/orbita-worker/{version}/` (ожидаемый размер ~50–80 МБ, включает .NET runtime).
3. После установки откроется **мастер настройки** — вставьте API-ключ.
4. Воркер уходит в **трей** и добавляется в **автозапуск Windows**.
5. В панели на странице воркера: включите нужные аккаунты AdsPower, Multilogin или «Обычный браузер», задайте параллелизм.

**Обновление:** повторный запуск MSI той же линейки **заменяет** установленную версию (как DeskLink Agent), а не ставится параллельно. Путь `%LocalAppData%\Orbita\Worker\` и API-ключ в `%LocalAppData%\OrbitaWorker\` сохраняются. Перед апдейтом MSI закрывает запущенный `Orbita.Worker.exe`.

Пути после установки:

- Программа: `%LocalAppData%\Orbita\Worker\Orbita.Worker.exe`
- Настройки (API-ключ): `%LocalAppData%\OrbitaWorker\`
- Данные воркера: `%LocalAppData%\OrbitaWorker\Data\`

Тихая установка для скриптов (без мастера):

```powershell
Orbita.Worker.exe --install --api-key <KEY>
```

### Сборка MSI

```bat
publish\build.bat -Target orbita-worker
```

Или через меню `publish\build.bat` → **Orbita Worker (Windows x64 MSI)**.

Результат: `publish/out/orbita-worker/{version}/Orbita.Worker.Setup-{version}.msi`

## API для воркеров

- `POST /api/v1/admin/workers/create` — создать воркер (Admin)
- `GET /api/v1/workers/config` — конфигурация (воркер), включая pending-проверку/синхронизацию провайдера
- `POST /api/v1/workers/accounts/sync` — синхронизация AdsPower/Multilogin-профилей (local-аккаунты не трогает)
- `POST /api/v1/workers/provider-checks` — воркер сообщает результат проверки AdsPower/Multilogin/Chrome
- `POST /api/v1/workers/{id}/provider-checks` — панель: поставить проверку на воркер
- `POST /api/v1/workers/{id}/provider-sync` — панель: немедленная синхронизация каталога AdsPower/Multilogin
- `POST /api/v1/workers/candidates` — отправка откликов
- `POST /api/v1/workers/heartbeat` — heartbeat + CPU/RAM
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
- Шаблон переменных для `.env` (рабочие значения хранить только на сервере):
  ```
  REGISTRATION_SECRET=replace-with-a-new-random-secret
  JWT_KEY=replace-with-a-new-random-signing-key-at-least-32-bytes
  ADMIN_PASSWORD=replace-with-a-new-strong-password
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
