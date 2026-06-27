#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
IMAGE_TAG="${1:-notifybot-api:prod}"

echo "Building ${IMAGE_TAG} from ${ROOT}"
docker build -f "${ROOT}/deploy/control-panel/Dockerfile.notifybot" -t "${IMAGE_TAG}" "${ROOT}"
echo "Done: ${IMAGE_TAG}"