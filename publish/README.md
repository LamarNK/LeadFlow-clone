# Publish / Build

## build.bat — LeadFlow и Orbita Worker (Windows)

Версионирование как в DeskLink: `publish\version-seed.json` (стартовые версии), `publish\state\versions.json` (последние собранные), артефакты в `publish\out\{target}\{version}\`.

По умолчанию bump **auto**: +revision; при revision ≥ 99 → +build. Явный bump:

```bat
publish\build.bat -Target orbita-worker -VersionBump minor
publish\build.bat -Target all -VersionBump revision -Clean
```

### LeadFlow (ZIP)

```text
publish\out\leadflow\1.0.0.1\LeadFlow-Windows-x64-1.0.0.1.zip
publish\out\leadflow\1.0.0.1\orbita-build.json
```

### Orbita Worker (MSI)

```text
publish\out\orbita-worker\1.0.0.1\Orbita.Worker.Setup-1.0.0.1.msi
```

Self-contained (~50–80 MB), per-user: `%LocalAppData%\Orbita\Worker\`. Повторная установка MSI **обновляет** существующую версию (MajorUpgrade + тот же UpgradeCode), не создаёт вторую копию.

Запуск:

```bat
publish\build.bat
publish\build.bat -Target leadflow
publish\build.bat -Target orbita-worker
publish\build.bat -Target all
```

Меню: multi-select (Space — выбрать несколько, Enter — собрать).

## publish.bat — Orbita + NotifyBot (сервер)

Деплой на VPS Orbita (`/opt/orbita`) через Docker:

- `orbita-api`
- `orbita-web`
- `notifybot`
- `config` — `docker-compose.images.yml`, `Caddyfile`, `backup-db.sh`
- `all` — всё выше

Запуск:

```bat
publish\publish.bat
publish\publish.bat orbita-api
publish\publish.bat notifybot
publish\publish.bat all
```

**Docker на ПК не нужен.** Образы собираются на сервере через SSH (`tar` + `deploy-remote-build.sh`). При сборке `orbita-web` клиентские файлы `Orbita.Web/wwwroot/js` автоматически обфусцируются; исходные файлы в репозитории не изменяются.

Как в DeskLink: вся логика деплоя в `publish.bat`, PowerShell только для меню (`publish-menu.ps1`). Отдельного `publish.ps1` нет — антивирус на него не ругается.

Переменные окружения:

| Переменная | По умолчанию |
|------------|--------------|
| `LEADFLOW_SERVER` | `root@163.5.153.207` |
| `LEADFLOW_SSH_PORT` | `22` |
| `LEADFLOW_SSH_KEY` | не задан — `Z:\servers\.ssh\home`, иначе стандартный SSH (`~/.ssh/config`, ssh-agent) |
| `LEADFLOW_SSH_CONNECT_TIMEOUT` | `15` секунд на установку соединения |
| `LEADFLOW_SSH_ALIVE_INTERVAL` | `15` секунд между keepalive-проверками |
| `LEADFLOW_SSH_ALIVE_COUNT_MAX` | `4` — оборвать неотвечающее соединение примерно через минуту |
| `LEADFLOW_SECRET_FILE` | `publish\secrets\orbita.env` |
| `LEADFLOW_SKIP_SECRETS_SYNC` | `0` — поставьте `1`, чтобы не копировать `.env` |
| `LEADFLOW_FORCE_PUBLISH` | `0` — поставьте `1`, чтобы принудительно залить всё, даже если файлы не менялись |

### Инкрементальная публикация

Перед загрузкой скрипт сравнивает хеши файлов с последним успешным деплоем (`publish\state\*.deploy.json`):

- **без изменений** — upload и сборка пропускаются;
- **мало изменений** — на сервер уходит delta-архив только с изменёнными файлами;
- **много изменений** (≥30% файлов или удаления) — полный архив, как раньше.

На сервере контекст кэшируется в `/opt/orbita/.build-cache/<service>/`, поэтому Docker переиспользует слои между деплоями.

Из staging исключаются `bin/`, `obj/`, `.vs/`, `.git/` и прочий мусор.

Секреты для сервера:

```bat
copy publish\secrets\orbita.env.example publish\secrets\orbita.env
```

Файл `orbita.env` не коммитится (только `orbita.env.example`).
