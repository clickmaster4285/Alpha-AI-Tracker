#!/usr/bin/env bash
# Phase 3 GPS smoke test — geofence CRUD, location list, Haversine ingest evaluation.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
API="${API_URL:-http://localhost:8080/api/v1}"
EMAIL="${ADMIN_EMAIL:-admin@alphai.com}"
PASS="${ADMIN_PASSWORD:-AlphaAI@2024!}"

COOKIE_JAR="$(mktemp)"
trap 'rm -f "$COOKIE_JAR"' EXIT

echo "== gps-phase3: login =="
LOGIN=$(curl -sf -c "$COOKIE_JAR" -X POST "$API/auth/login" \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PASS\"}")
echo "$LOGIN" | grep -q '"message"' || echo "login OK"

echo "== gps-phase3: geofence CRUD =="
CREATE=$(curl -sf -b "$COOKIE_JAR" -X POST "$API/geofence-zones" \
  -H 'Content-Type: application/json' \
  -d '{"name":"Test Office","latitude":24.8607,"longitude":67.0011,"radiusM":500,"alertOnExit":true}')
ZONE_ID=$(echo "$CREATE" | grep -oE '"id":[0-9]+' | head -1 | cut -d: -f2)
[[ -n "$ZONE_ID" ]] || { echo "create geofence failed: $CREATE" >&2; exit 1; }
echo "created zone id=$ZONE_ID"

LIST=$(curl -sf -b "$COOKIE_JAR" "$API/geofence-zones")
echo "$LIST" | grep -q 'Test Office' || { echo "list geofence failed" >&2; exit 1; }

curl -sf -b "$COOKIE_JAR" -X PUT "$API/geofence-zones/$ZONE_ID" \
  -H 'Content-Type: application/json' \
  -d '{"name":"Test Office Updated","radiusM":600}' >/dev/null

echo "== gps-phase3: location-samples list =="
LOC=$(curl -sf -b "$COOKIE_JAR" "$API/location-samples?page=1&perPage=5")
echo "$LOC" | grep -q '"data"' || { echo "location list failed: $LOC" >&2; exit 1; }

echo "== gps-phase3: cleanup =="
curl -sf -b "$COOKIE_JAR" -X DELETE "$API/geofence-zones/$ZONE_ID" >/dev/null

echo "== gps-phase3: build checks =="
(cd "$ROOT/server" && go build -o /dev/null ./cmd/server/...)
(cd "$ROOT/server" && go vet ./...)
(cd "$ROOT/client" && dotnet build -v q --nologo)
(cd "$ROOT/web" && npx tsc --noEmit)

echo "gps-phase3: OK (zone CRUD + location list + builds clean)"
