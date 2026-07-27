# LeadFlow

LeadFlow helps recruiting teams process `Avito` responses as an operational flow: collect candidates, prevent duplicates, send suitable candidates to `Bitrix24`, and monitor the result.

The repository contains the Windows client, the distributed **Orbita** control panel and worker, and **NotifyBot** for forwarding 3DS SMS notifications.

## Capabilities

- collecting and monitoring new responses across multiple Avito accounts and browser profiles;
- duplicate checks in the local database and Bitrix24;
- sending candidates to Bitrix24 and tracking processing outcomes;
- response, worker, account, ad and error analytics;
- response-phone observations: detect phone-number changes and expose their metrics in Orbita;
- central management of distributed workers and AdsPower accounts;
- an optional NotifyBot integration for 3DS SMS notifications.

## Components

| Component | Purpose |
| --- | --- |
| `LeadFlow` | Windows desktop application for daily response processing. |
| `LeadFlow.Core` | Shared domain and application logic. |
| `Orbita.Api` | REST API and PostgreSQL persistence for the control plane. |
| `Orbita.Web` | Web control panel for workers, responses and analytics. |
| `Orbita.Worker` | Windows tray worker that collects Avito responses and sends them to Orbita. |
| `src/NotifyBot.*` | Service for routing Plusofon 3DS SMS notifications to Telegram. |

## Requirements

- .NET SDK 10;
- Windows for `LeadFlow` and `Orbita.Worker`;
- Docker Desktop (optional, for the local Orbita stack);
- PostgreSQL 16 when running the API outside Docker.

## Quick start: Orbita

Start PostgreSQL, API and web panel locally:

```bash
docker compose -f deploy/control-panel/docker-compose.yml up --build
```

The panel becomes available at `http://localhost:8081`, and the API at `http://localhost:8080`.

For development without the full Docker stack:

```bash
dotnet run --project Orbita.Api
dotnet run --project Orbita.Web
```

## Build

Open `LeadFlow.slnx` in Visual Studio or build the solution from the command line:

```bash
dotnet build LeadFlow.slnx
dotnet test LeadFlow.Tests/LeadFlow.Tests.csproj
dotnet test Orbita.Tests/Orbita.Tests.csproj
```

Windows packages are built through the scripts in [`publish/`](publish/README.md):

```bat
publish\build.bat -Target leadflow
publish\build.bat -Target orbita-worker
```

## Documentation

- [User guide](docs/USER_GUIDE_RU.md)
- [Orbita architecture and local setup](docs/ORBITA.md)
- [Build and deployment scripts](publish/README.md)

## Configuration and security

Do not commit production credentials, session data or local configuration. Use the supplied `.env.example` files as templates and keep actual values in ignored local files.
