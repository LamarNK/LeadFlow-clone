#!/usr/bin/env bash
set -euo pipefail

prune_docker_artifacts() {
  if [[ "${LEADFLOW_SKIP_DOCKER_PRUNE:-0}" == "1" ]]; then
    echo "Docker prune skipped (LEADFLOW_SKIP_DOCKER_PRUNE=1)."
    return 0
  fi

  echo "== Pruning dangling Docker images =="
  docker image prune -f

  if [[ "${LEADFLOW_DOCKER_PRUNE_DEEP:-0}" == "1" ]]; then
    echo "== Deep prune: unused images =="
    docker image prune -a -f

    echo "== Deep prune: build cache older than 7 days =="
    if ! docker builder prune -f --filter until=168h; then
      echo "Build cache prune skipped (BuildKit unavailable)."
    fi
  fi
}

compute_cache_bust() {
  if [[ -n "${LEADFLOW_CACHE_BUST:-}" ]]; then
    echo "$LEADFLOW_CACHE_BUST"
    return
  fi

  if command -v sha256sum >/dev/null 2>&1; then
    local -a search_paths=()
    for path in Orbita.Api Orbita.Contracts Orbita.Logging Orbita.Web src/NotifyBot.Api src/NotifyBot.Application src/NotifyBot.Domain src/NotifyBot.Infrastructure; do
      if [[ -d "$path" ]]; then
        search_paths+=("$path")
      fi
    done

    if [[ ${#search_paths[@]} -gt 0 ]]; then
      find "${search_paths[@]}" -type f 2>/dev/null \
        | LC_ALL=C sort \
        | xargs -r sha256sum \
        | sha256sum \
        | awk '{print $1}'
      return
    fi
  fi

  date +%s
}

build_docker_image() {
  export DOCKER_BUILDKIT=1

  local cache_bust
  cache_bust="$(compute_cache_bust)"

  local -a build_args=(
    --build-arg BUILDKIT_INLINE_CACHE=1
    --build-arg "CACHE_BUST=${cache_bust}"
    -f "$dockerfile_rel"
    -t "$image_tag"
  )

  echo "Docker build cache-bust=${cache_bust}"
  docker build --progress=plain "${build_args[@]}" .
}

acquire_deploy_lock() {
  local lock_file="${LEADFLOW_DEPLOY_LOCK_FILE:-$remote_dir/.leadflow-deploy.lock}"

  if ! command -v flock >/dev/null 2>&1; then
    echo "flock is required to serialize Orbita deployments."
    exit 1
  fi

  echo "== Waiting for deployment lock: $lock_file =="
  exec 9>"$lock_file"
  flock -x 9
  echo "== Acquired deployment lock: $lock_file =="
}

context_archive="${1:?context archive is required}"
dockerfile_rel="${2:?dockerfile path is required}"
image_tag="${3:?image tag is required}"
service="${4:?compose service is required}"
remote_dir="${5:-/opt/orbita}"
deploy_mode="${6:-full}"

echo "== Remote deploy: service=${service} mode=${deploy_mode} =="

# GNU tar: exit 0 = ok, 1 = warnings only (e.g. "timestamp in the future"), 2 = fatal.
# Client clock ahead of the server triggers exit 1; set -e must not treat that as failure.
tar_extract() {
  local archive="$1"
  local dest="$2"
  local rc=0

  set +e
  if tar --warning=no-timestamp -xzf "$archive" -C "$dest" 2>/dev/null; then
    rc=0
  else
    rc=$?
    if [[ $rc -ne 0 ]]; then
      tar -xzf "$archive" -C "$dest"
      rc=$?
    fi
  fi
  set -e

  if [[ $rc -le 1 ]]; then
    return 0
  fi
  return $rc
}

if [[ ! -f "$context_archive" ]]; then
  echo "Context archive not found: $context_archive"
  exit 1
fi

if [[ ! -d "$remote_dir" ]]; then
  echo "Remote directory not found: $remote_dir"
  exit 1
fi

cache_dir="$remote_dir/.build-cache/$service"
extract_dir="/tmp/leadflow-extract-$$"
mkdir -p "$cache_dir" "$extract_dir"

if [[ "$deploy_mode" == "full" ]]; then
  rm -rf "$cache_dir"
  mkdir -p "$cache_dir"
  tar_extract "$context_archive" "$cache_dir"
else
  tar_extract "$context_archive" "$extract_dir"

  if [[ -f "$extract_dir/deleted.txt" ]]; then
    while IFS= read -r rel || [[ -n "$rel" ]]; do
      [[ -z "$rel" ]] && continue
      rm -rf "$cache_dir/$rel"
    done < "$extract_dir/deleted.txt"
    rm -f "$extract_dir/deleted.txt"
  fi

  (
    cd "$extract_dir"
    set +e
    tar --warning=no-timestamp -cf - . 2>/dev/null | tar --warning=no-timestamp -xf - -C "$cache_dir" 2>/dev/null
    merge_rc=${PIPESTATUS[1]:-0}
    if [[ $merge_rc -gt 1 ]]; then
      tar -cf - . | tar -xf - -C "$cache_dir"
      merge_rc=${PIPESTATUS[1]:-0}
    fi
    set -e
    if [[ $merge_rc -gt 1 ]]; then
      exit "$merge_rc"
    fi
  )
fi

rm -rf "$extract_dir" "$context_archive"

cd "$cache_dir"

if [[ ! -f "$dockerfile_rel" ]]; then
  if [[ "$deploy_mode" == "delta" ]]; then
    echo "Build cache incomplete for delta deploy; missing $dockerfile_rel"
    echo "Retrying with a full upload is required."
    exit 42
  fi
  echo "Dockerfile not found in context: $dockerfile_rel"
  exit 1
fi

build_docker_image
acquire_deploy_lock
compose_src="$cache_dir/deploy/control-panel/docker-compose.images.yml"
if [[ -f "$compose_src" ]]; then
  cp "$compose_src" "$remote_dir/docker-compose.images.yml"
  cp "$compose_src" "$remote_dir/docker-compose.yml"
  echo "Updated docker-compose.images.yml and docker-compose.yml in $remote_dir"
fi

cd "$remote_dir"

if [[ ! -f "docker-compose.images.yml" ]]; then
  echo "docker-compose.images.yml not found in $remote_dir"
  exit 1
fi

if [[ ! -f ".env" ]]; then
  echo ".env not found in $remote_dir"
  exit 1
fi

if [[ "$service" == "notifybot-api" ]]; then
  docker compose -f docker-compose.images.yml --env-file .env up -d --force-recreate notifybot-postgres notifybot-api
elif [[ "$service" == "web" ]]; then
  docker compose -f docker-compose.images.yml --env-file .env up -d --force-recreate --no-deps "$service"
else
  docker compose -f docker-compose.images.yml --env-file .env up -d --force-recreate "$service"
fi

docker compose -f docker-compose.images.yml ps

prune_docker_artifacts

echo "Built and deployed $image_tag ($service) using $deploy_mode context"
