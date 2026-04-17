#!/usr/bin/env bash
# smoke-and-rollback.sh <health-url>
#
# Post-deploy smoke test. Polls the supplied health URL up to 30 times with 2-second intervals
# (total wall-time: ~60s). Exits 0 when /health/ready returns HTTP 200. On persistent non-200,
# pulls the previous image digest from .previous-tag and restarts the compose stack, then fails
# the deploy so the CI workflow surfaces a red X.
#
# Usage:
#   bash scripts/deploy/smoke-and-rollback.sh "https://contracts.kingsleyonoh.com/health/ready"
#
# Environment:
#   STATE_DIR      (default /opt/contract-engine)       dir holding .previous-tag
#   COMPOSE_FILE   (default $STATE_DIR/docker-compose.prod.yml)
#   MAX_ATTEMPTS   (default 30)                         total probe attempts
#   SLEEP_SECONDS  (default 2)                          wait between attempts

set -euo pipefail

HEALTH_URL="${1:-}"
if [ -z "${HEALTH_URL}" ]; then
  echo "usage: $0 <health-url>" >&2
  exit 2
fi

STATE_DIR="${STATE_DIR:-/opt/contract-engine}"
STATE_FILE="${STATE_DIR}/.previous-tag"
COMPOSE_FILE="${COMPOSE_FILE:-${STATE_DIR}/docker-compose.prod.yml}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-30}"
SLEEP_SECONDS="${SLEEP_SECONDS:-2}"

echo "post-deploy smoke test: ${HEALTH_URL}"
echo "  max attempts: ${MAX_ATTEMPTS}, interval: ${SLEEP_SECONDS}s"

attempt=0
while [ "${attempt}" -lt "${MAX_ATTEMPTS}" ]; do
  attempt=$((attempt + 1))
  HTTP_CODE=$(curl --silent --output /dev/null --write-out "%{http_code}" --max-time 10 "${HEALTH_URL}" || echo "000")

  if [ "${HTTP_CODE}" = "200" ]; then
    echo "health probe OK (attempt ${attempt}/${MAX_ATTEMPTS}) — deploy succeeded"
    exit 0
  fi

  echo "  attempt ${attempt}/${MAX_ATTEMPTS}: HTTP ${HTTP_CODE} — retrying in ${SLEEP_SECONDS}s..."
  sleep "${SLEEP_SECONDS}"
done

echo "::error::health probe never returned 200 after ${MAX_ATTEMPTS} attempts — rolling back"

# Attempt rollback. If we have a prior image digest, re-tag latest to it and restart.
if [ ! -f "${STATE_FILE}" ]; then
  echo "::error::no ${STATE_FILE} found — cannot roll back automatically"
  exit 1
fi

PREVIOUS=$(cat "${STATE_FILE}")
if [ "${PREVIOUS}" = "NONE" ] || [ -z "${PREVIOUS}" ]; then
  echo "::error::previous tag marker is NONE (cold deploy) — cannot roll back automatically"
  exit 1
fi

echo "rolling back to ${PREVIOUS}..."
if docker tag "${PREVIOUS}" ghcr.io/kingsleyonoh/contract-lifecycle-engine:latest; then
  docker compose -f "${COMPOSE_FILE}" up -d app
  echo "rollback complete — previous image restored to :latest"
else
  echo "::error::docker tag failed — image ${PREVIOUS} may no longer exist locally"
fi

exit 1
