#!/usr/bin/env bash
# generate-windows-vars.sh — Create a temporary Inno Setup include file
# from APP_IDENTIFIERS so the .iss script doesn't hardcode app metadata.
# Output: publish/windows/windows_vars.iss
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

if [ ! -f "$PROJECT_DIR/APP_IDENTIFIERS" ]; then
  echo "ERROR: $PROJECT_DIR/APP_IDENTIFIERS not found"
  exit 1
fi

OUT="$SCRIPT_DIR/windows/windows_vars.iss"
mkdir -p "$(dirname "$OUT")"

# shellcheck disable=SC1090
source "$PROJECT_DIR/APP_IDENTIFIERS"

cat > "$OUT" <<EOF
; Auto-generated from APP_IDENTIFIERS — do not edit manually
#define MyAppName "$DISPLAY_NAME"
#define MyAppPublisher "$PUBLISHER"
#define MyAppURL "$APP_URL"
#define MyAppExeName "$EXECUTABLE_NAME.exe"
#define APP_MUTEX "$APP_MUTEX"
#define WINDOWS_INSTALLER_NAME "$WINDOWS_INSTALLER_NAME"
EOF

echo "Generated $OUT"