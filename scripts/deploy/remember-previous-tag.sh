#!/usr/bin/env bash
# remember-previous-tag.sh
#
# Captures the currently-running `app` container's image digest so smoke-and-rollback.sh can
# revert to it if the next deploy fails its /health/ready probe. Runs BEFORE `docker compose
# pull app` so the "previous" digest is the one actually serving traffic at the moment we
# take the snapshot.
#
# Idempotent: if the container is not running (cold deploy), writes a sentinel line so the
# rollback script treats a missing previous tag as "no rollback target — fail the deploy
# instead of pulling unknown image".

set -euo pipefail

STATE_DIR="${STATE_DIR:-/opt/contract-engine}"
STATE_FILE="${STATE_DIR}/.previous-tag"
COMPOSE_FILE="${COMPOSE_FILE:-${STATE_DIR}/docker-compose.prod.yml}"

mkdir -p "${STATE_DIR}"

# Resolve the running app container's image reference (falls back to empty if stack is cold).
CURRENT_IMAGE=""
if CONTAINER_ID=$(docker compose -f "${COMPOSE_FILE}" ps -q app 2>/dev/null); then
  if [ -n "${CONTAINER_ID}" ]; then
    CURRENT_IMAGE=$(docker inspect --format='{{.Image}}' "${CONTAINER_ID}" 2>/dev/null || echo "")
  fi
fi

if [ -z "${CURRENT_IMAGE}" ]; then
  echo "no previous container running — writing sentinel to ${STATE_FILE}"
  echo "NONE" > "${STATE_FILE}"
  exit 0
fi

echo "${CURRENT_IMAGE}" > "${STATE_FILE}"
echo "recorded previous image digest: ${CURRENT_IMAGE}"
