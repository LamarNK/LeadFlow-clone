# Orbita Telephony Gateway

`Orbita.TelephonyGateway` is an optional ingress service in front of the existing
Orbita telephony webhook. It does not register SIP trunks or change call routing.
Its only job is to deliver provider events to `Orbita.Api` and keep a locally
encrypted retry queue when the API is temporarily unavailable.

The optional `asterisk` container is a separate SIP/media layer. It registers
at a provider, records RTP with Asterisk `MixMonitor`, and uploads call metadata
plus the WAV file through this gateway. `Orbita.Api` stores that file in a
private volume; `Orbita.Web` streams it only after checking access to the CRM
card. Unanswered calls are also delivered to the CRM feed; they simply have no
audio file.

The Compose service is behind the `telephony` profile and is therefore not
started by a regular deployment.

## Before enabling it

1. Rotate every provider key or SIP password that has ever been sent through a
   chat, screenshot, ticket, or source file.
2. Generate a queue key from exactly 32 random bytes:

   ```powershell
   [Convert]::ToBase64String(
       [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
   ```

3. Put the generated value in the server-side `.env` as
   `TELEPHONY_GATEWAY_QUEUE_KEY`. Never commit the populated `.env`.
4. Route `https://telephony.orbitsu.ru` to the gateway's loopback port in the
   existing TLS reverse proxy: `127.0.0.1:8084` for
   `docker-compose.images.yml`, or `127.0.0.1:8082` for the build-based
   `docker-compose.server.yml`. Do not expose either port directly to the
   Internet.
5. Set `TELEPHONY_GATEWAY_PUBLIC_BASE_URL=https://telephony.orbitsu.ru`.

## Local start

```powershell
docker compose --profile telephony `
  -f deploy/control-panel/docker-compose.yml up -d --build
```

The local health endpoints are:

- `http://localhost:8082/health/live`
- `http://localhost:8082/health/ready`

The development Compose file contains a non-secret deterministic queue key.
It must never be used in production.

The queue is bounded both by item count (`TELEPHONY_GATEWAY_MAX_QUEUE_ITEMS`)
and total bytes (`TELEPHONY_GATEWAY_MAX_QUEUE_BYTES`). Recording uploads are
bounded by `TELEPHONY_GATEWAY_MAX_BODY_BYTES` and by the API recording limit.

## Server start

Starting the service does not redirect any provider callbacks by itself:

```bash
docker compose --profile telephony \
  -f docker-compose.server.yml --env-file .env up -d --build telephony-gateway
```

For the production image compose, first build/load the
`orbita-telephony-gateway:prod` image from `Dockerfile.telephony-gateway`, then
start it with:

```bash
docker compose --profile telephony \
  -f docker-compose.images.yml --env-file .env up -d telephony-gateway api
```

After both health checks pass, open the Orbita telephony settings and rotate the
receiver for the selected provider. The newly generated callback uses
`TELEPHONY_GATEWAY_PUBLIC_BASE_URL`. Test one internal number before changing
the rest of the office.

## Plusofon

1. In **Settings → office → Telephony → Plusofon**, fill the SIP server, login,
   optional authorization login, password and active outgoing caller ID. The
   password is encrypted in the database and the Asterisk runtime configuration
   is updated without putting credentials in Git.
2. Keep TCP, port 5060 and the server/domain issued for the same Plusofon
   contract. Assign the same active caller ID in the Plusofon cabinet.
3. In **Telephony → SIP server**, bind unique extensions such as 201 and 202 to
   employees. An inbound callback is first offered to the employee who most
   recently called that client during the 30-day affinity window.
4. The provider webhook and recording API are optional when Asterisk is the
   authoritative call source. If provider-side records are required, create a
   webhook for the `destroy` event and all calls, then configure the fields
   `call_id`, `direction`, `from`, `to`, `internal`, `timestamp`, and `duration`.
5. Save the Plusofon `Client ID` and API access token. The token is encrypted
   with the persistent Orbita Data Protection key ring and is never returned by
   the API after saving.

Do not enable both the Asterisk call publisher and the Plusofon webhook for the
same line unless cross-provider deduplication is implemented: their external
call identifiers differ, so the same conversation can otherwise appear twice.

Plusofon publishes the recording through a separate API method. When a
`destroy` event arrives, Orbita saves the call immediately and a background
worker requests `GET /api/v1/call/{call_id}/record`. A recording that is not yet
ready is retried with bounded backoff; authentication failures stop retries and
are written to logs without the token.

## Own SIP/media container

The PBX is disabled during a regular deploy and belongs to the separate
`telephony-media` Compose profile.

1. In Orbita open **Settings → office → Telephony → SIP server**, create a
   receiver and put its Public ID and one-time secret into the protected server
   The API writes the one-time receiver credentials to the shared runtime volume
   as `receiver.<office-id>.conf`. Do not copy one office secret over another.
2. Set the PBX public/NAT address and a default manager extension.
3. Bind each internal extension to the matching Orbita user on the same settings
   page.

Required server `.env` values:

```dotenv
ASTERISK_BIND_IP=PUBLIC_SERVER_IP
ASTERISK_EXTERNAL_ADDRESS=PUBLIC_SERVER_IP
ASTERISK_LOCAL_NET=172.16.0.0/12
ASTERISK_DEFAULT_EXTENSION=201
# All office SIP lines are configured in Orbita, not here.
ASTERISK_PRIMARY_TRUNK_ENABLED=false
```

Create and edit each Beeline line or the Plusofon SIP line in
**Settings → Telephony**. For Plusofon, enter the SIP server, domain, login,
authorization login, password, outbound caller ID, port and transport in that
form. Orbita encrypts the SIP password in its database, publishes an
office-specific runtime config to the shared volume, and Asterisk reloads it
automatically. No office SIP login, password, proxy or registrar belongs in the
server `.env`.

`ASTERISK_PRIMARY_TRUNK_ENABLED=true` is a compatibility option for one
separate, non-office legacy provider. Only in that case also set
`ASTERISK_SIP_SERVER`, `ASTERISK_SIP_PORT`, `ASTERISK_SIP_TRANSPORT`,
`ASTERISK_SIP_LOGIN`, optional `ASTERISK_SIP_AUTH_LOGIN`,
`ASTERISK_SIP_PASSWORD`, and `ASTERISK_DEFAULT_OFFICE_ID`. Never duplicate an
office-managed Beeline account in those variables: two registrations of the
same account cause provider rejections.

Browser SIP endpoints are also generated per employee from the settings page.
`ASTERISK_ENDPOINTS_FILE` is optional and is only for physical desk phones or
other legacy SIP clients that cannot use the browser endpoint.

Start the source deployment only after those values are ready:

```bash
docker compose --profile telephony-media \
  -f docker-compose.server.yml --env-file .env \
  up -d --build api telephony-gateway asterisk
```

For `docker-compose.images.yml`, build and transport the
`orbita-asterisk:prod` image from `Dockerfile.asterisk` alongside the other
production images first.

Open UDP 5060 for approved manager endpoints, TCP or UDP 5060 for the selected
provider transport, and UDP 10000–10100 for RTP only after restricting the
firewall to provider and approved manager networks. Do not publish AMI/ARI.
Office SIP passwords are encrypted in Orbita and copied only to Asterisk's
private generated runtime config; browser clients never receive them.

The container listens for manager endpoints on UDP 5060 and can register the
upstream provider by either TCP or UDP. For the current Plusofon pilot select
TCP and port 5060 in the interface. The generated line uses a 300-second
registration lifetime and only PCMA/PCMU (G.711A/U). The server must use the
public IP range approved for the Plusofon account; do not route SIP or RTP
through a VPN unless that egress address has been approved.

Container health checks only prove that Asterisk itself is running. Confirm the
actual provider registration separately:

```bash
docker compose --profile telephony-media \
  -f docker-compose.server.yml --env-file .env \
  exec asterisk /opt/orbita-asterisk/provider-ready.sh
```

The command is safe to paste into a ticket: it prints registration status but
never the SIP password. Do not place the generated `pjsip.conf` output in a
ticket because it contains the provider password.

Before the first call, run the complete non-secret readiness check:

```bash
docker compose --profile telephony-media \
  -f docker-compose.server.yml --env-file .env \
  exec asterisk /opt/orbita-asterisk/pilot-readiness.sh
```

It verifies the Asterisk process, gateway, provider registration, configured
default manager endpoint, and writable recording spool. It does not originate
a call and does not change provider or CRM state.

### Calling from an Orbita card (WebRTC)

The browser softphone is optional and is disabled unless
`TELEPHONY_WEBRTC_ENABLED=true`. When enabled, an authenticated CRM user can
request only their own SIP endpoint. Orbita never sends the upstream provider
trunk password to the browser. The card action **Call in Orbita** registers the
user's WebRTC endpoint in Asterisk, originates an audio-only call, and shows a
small call-status panel in the top-right corner.

Local pilot values:

```dotenv
TELEPHONY_WEBRTC_ENABLED=true
TELEPHONY_WEBRTC_WS_URL=ws://127.0.0.1:8088/ws
TELEPHONY_WEBRTC_SIP_DOMAIN=127.0.0.1
```

Bind an internal number to the matching employee in the office telephony
settings. Orbita generates a unique WebRTC login and password, encrypts the
password in its database, and publishes the endpoint to the shared Asterisk
runtime configuration. The API resolves that binding against the current JWT
user; another employee cannot request these credentials by changing a query
string. Per-employee credentials do not belong in `.env`.

For production, do not expose plain `ws://` or Asterisk HTTP directly. Terminate
TLS at the existing reverse proxy, publish a dedicated `wss://` endpoint, and
limit the SIP/RTP firewall rules. The page itself must also be served over HTTPS so that browsers allow
microphone access. Test one internal employee and one controlled phone number
before enabling the action for the whole office.

Local verification:

1. Open `http://127.0.0.1:8081` and sign in as the employee bound to extension
   `201`.
2. Open a test candidate card, choose the phone menu, and press **Call in
   Orbita**.
3. Allow microphone access, wait for the top-right panel to report registration,
   and end the controlled call from that panel.
4. Wait for the gateway retry interval and confirm that the call and recording
   appear in the same candidate card feed.

Do not use a real candidate number for the first verification. Opening the card
or fetching the WebRTC configuration is read-only, but pressing the call action
originates a real provider call.

If the API is unavailable, the gateway stores the complete upload in its
encrypted bounded queue. If the gateway itself is unavailable, Asterisk keeps
the metadata and any WAV file and retries every 30 seconds. The local WAV is removed
only after a successful response.

The runtime directory can contain isolated Plusofon and Beeline trunks for
multiple offices. Employee routes select the configured office endpoint; if an
office-specific endpoint is absent, routing falls back to the optional legacy
`orbita-provider` trunk. Provider authentication assumptions remain separate.

## Failure behaviour

- `2xx` from `Orbita.Api`: the provider receives the original response.
- `4xx` from `Orbita.Api`: the error is returned to the provider and is not
  retried because the payload or credentials are invalid.
- timeout, `408`, `425`, `429`, or `5xx`: the event is encrypted with AES-256-GCM,
  written atomically to the persistent queue volume, and the provider receives
  `202 Accepted`.
- exhausted retries: the encrypted file is kept with a `.dead` extension for
  investigation. Its contents must never be copied into logs or tickets.

The webhook paths remain compatible with the current Orbita API:

`/api/v1/integrations/telephony/sipout/{publicId}`

`/api/v1/integrations/telephony/plusofon/{publicId}`

`/api/v1/integrations/telephony/asterisk/{publicId}`

The persistent volume `orbita_crm_call_recordings` contains personal
communications. Apply restricted access, encrypted backups and an explicit
retention policy.

## Rollback

Disable `TELEPHONY_GATEWAY_PUBLIC_BASE_URL`, redeploy `Orbita.Api`, and rotate
the affected provider receiver again. The generated callback will point
directly to the normal public API URL. Existing CRM call records are not
removed.
