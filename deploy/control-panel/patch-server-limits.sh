#!/bin/bash
set -euo pipefail
cd /opt/orbita

cp docker-compose.images.yml "docker-compose.images.yml.bak-$(date +%Y%m%d%H%M%S)"

python3 <<'PY'
from pathlib import Path

path = Path("docker-compose.images.yml")
text = path.read_text()

upload = """      Kestrel__Limits__MaxRequestBodySize: \"536870912\"
      Kestrel__Limits__MinRequestBodyDataRate__BytesPerSecond: \"100\"
      Kestrel__Limits__MinRequestBodyDataRate__GracePeriod: \"00:10:00\"
      WorkerReleases__MaxUploadBytes: \"536870912\""""

if "Kestrel__Limits__MaxRequestBodySize" not in text:
    text = text.replace(
        "      Logs__SharedRoot: /app/Data/logs\n    depends_on:\n      postgres:",
        "      Logs__SharedRoot: /app/Data/logs\n" + upload + "\n    depends_on:\n      postgres:",
        1,
    )
    text = text.replace(
        "    volumes:\n      - orbita_logs:/app/Data/logs\n\n  web:",
        "    volumes:\n      - orbita_logs:/app/Data/logs\n      - orbita_releases:/app/Data/releases\n\n  web:",
        1,
    )
    text = text.replace(
        "      Logs__SharedRoot: /app/Data/logs\n    depends_on:\n      - api",
        "      Logs__SharedRoot: /app/Data/logs\n" + upload + "\n    depends_on:\n      - api",
        1,
    )
    text = text.replace(
        "volumes:\n  orbita_pg:\n  orbita_logs:\n  orbita_dataprotection:",
        "volumes:\n  orbita_pg:\n  orbita_logs:\n  orbita_releases:\n  orbita_dataprotection:",
        1,
    )

path.write_text(text)
print("compose updated")
PY

cp Caddyfile "Caddyfile.bak-$(date +%Y%m%d%H%M%S)"
cat > Caddyfile <<'EOF'
{
	email admin@orbitsu.ru
	acme_ca https://acme-v02.api.letsencrypt.org/directory
}

orbitsu.ru, www.orbitsu.ru {
	request_body {
		max_size 512MB
	}

	reverse_proxy 127.0.0.1:8081 {
		transport http {
			read_timeout 30m
			write_timeout 30m
		}
	}
}

api.orbitsu.ru {
	request_body {
		max_size 512MB
	}

	reverse_proxy 127.0.0.1:8082 {
		transport http {
			read_timeout 30m
			write_timeout 30m
		}
	}
}

http://163.5.153.207 {
	request_body {
		max_size 512MB
	}

	reverse_proxy 127.0.0.1:8081 {
		transport http {
			read_timeout 30m
			write_timeout 30m
		}
	}
}

:8080 {
	reverse_proxy 127.0.0.1:8082
}
EOF

if [ -f nginx-orbitsu.ru.conf ]; then
  cp nginx-orbitsu.ru.conf "/etc/nginx/sites-enabled/orbitsu.ru.bak-$(date +%Y%m%d%H%M%S)"
  cp nginx-orbitsu.ru.conf /etc/nginx/sites-enabled/orbitsu.ru
fi

if [ -f nginx-orbitsu-sni-443.conf ]; then
  mkdir -p /etc/nginx/stream-conf.d
  cp nginx-orbitsu-sni-443.conf /etc/nginx/stream-conf.d/orbitsu-sni-443.conf
fi

if systemctl is-active --quiet nginx; then
  nginx -t
  systemctl reload nginx
elif systemctl is-enabled --quiet caddy 2>/dev/null; then
  cp Caddyfile /etc/caddy/Caddyfile
  caddy validate --config /etc/caddy/Caddyfile
  systemctl reload caddy
fi

docker compose -f docker-compose.images.yml --env-file .env up -d api web

sleep 3
echo "=== API env ==="
docker exec orbita-api-1 printenv | grep -E 'Kestrel|WorkerReleases' || true
echo "=== WEB env ==="
docker exec orbita-web-1 printenv | grep -E 'Kestrel|WorkerReleases' || true
echo "=== API mounts ==="
docker inspect orbita-api-1 --format '{{range .Mounts}}{{.Destination}} <- {{.Name}}{{"\n"}}{{end}}'