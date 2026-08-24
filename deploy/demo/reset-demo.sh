#!/usr/bin/env bash
# Nightly reset for the public Atlas Community demo.
#
# Wipes the ephemeral SQLite volume, pulls the newest image, and brings the stack back up on a
# clean database. Idempotent and safe to run any time. Driven by atlas-demo-reset.timer.
set -euo pipefail

COMPOSE_DIR="${COMPOSE_DIR:-/opt/atlas-community-demo/deploy/demo}"

# Use whichever engine is installed (the reference host runs podman); override with COMPOSE="docker compose".
if [ -z "${COMPOSE:-}" ]; then
  if command -v podman >/dev/null 2>&1; then COMPOSE="podman compose"
  elif command -v docker >/dev/null 2>&1; then COMPOSE="docker compose"
  else echo "no podman or docker found" >&2; exit 1
  fi
fi

cd "$COMPOSE_DIR"

echo "[$(date -Is)] resetting Atlas demo with '$COMPOSE': down -v (wipes demo-data volume)"
$COMPOSE down -v

echo "[$(date -Is)] pulling newest image"
$COMPOSE pull

echo "[$(date -Is)] starting clean"
$COMPOSE up -d

echo "[$(date -Is)] demo reset complete"
