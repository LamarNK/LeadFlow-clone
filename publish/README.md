# Publish / Build

## build.bat — только LeadFlow (Windows)

Собирает десктопное приложение в архив:

```text
dist\LeadFlow-Windows-x64-Release.zip
```

Структура архива:

```text
LeadFlow-Windows-x64-Release/
  LeadFlow\LeadFlow.exe
  Документация\README.md
  Документация\USER_GUIDE_RU.md
  Как запустить.txt
```

Запуск:

```bat
publish\build.bat
publish\build.bat -Target leadflow
```

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

**Docker на ПК не нужен.** Образы собираются на сервере через SSH (`tar` + `deploy-remote-build.sh`).

Как в DeskLink: вся логика деплоя в `publish.bat`, PowerShell только для меню (`publish-menu.ps1`). Отдельного `publish.ps1` нет — антивирус на него не ругается.

Переменные окружения:

| Переменная | По умолчанию |
|------------|--------------|
| `LEADFLOW_SERVER` | `root@163.5.153.207` |
| `LEADFLOW_SSH_PORT` | `22` |
| `LEADFLOW_SSH_KEY` | не задан — используется стандартный SSH (`~/.ssh/config`, ssh-agent) |
| `LEADFLOW_SECRET_FILE` | `publish\secrets\orbita.env` |
| `LEADFLOW_SKIP_SECRETS_SYNC` | `0` — поставьте `1`, чтобы не копировать `.env` |

Секреты для сервера:

```bat
copy publish\secrets\orbita.env.example publish\secrets\orbita.env
```

Файл `orbita.env` не коммитится (только `orbita.env.example`).