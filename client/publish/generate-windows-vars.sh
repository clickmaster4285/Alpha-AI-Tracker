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

# Fallbacks if older APP_IDENTIFIERS omit the DLP keys.
: "${WINDOWS_SERVICE_NAME:=${APP_MUTEX}.Service}"
: "${WINDOWS_SERVICE_DISPLAY_NAME:=${DISPLAY_NAME} DLP}"
: "${WINDOWS_DLP_PIPE_NAME:=${APP_MUTEX}.Dlp}"
: "${WINDOWS_SERVICE_MAINT_TASK:=${APP_MUTEX}SvcMaint}"

cat > "$OUT" <<EOF
; Auto-generated from APP_IDENTIFIERS — do not edit manually
#define MyAppName "$DISPLAY_NAME"
#define MyAppPublisher "$PUBLISHER"
#define MyAppURL "$APP_URL"
#define MyAppExeName "$EXECUTABLE_NAME.exe"
#define APP_MUTEX "$APP_MUTEX"
#define WINDOWS_INSTALLER_NAME "$WINDOWS_INSTALLER_NAME"
#define WINDOWS_SERVICE_NAME "$WINDOWS_SERVICE_NAME"
#define WINDOWS_SERVICE_DISPLAY_NAME "$WINDOWS_SERVICE_DISPLAY_NAME"
#define WINDOWS_DLP_PIPE_NAME "$WINDOWS_DLP_PIPE_NAME"
#define WINDOWS_SERVICE_MAINT_TASK "$WINDOWS_SERVICE_MAINT_TASK"
#define WINDOWS_SERVICE_EXE_NAME "$WINDOWS_SERVICE_NAME.exe"
EOF

echo "Generated $OUT"

# MSBuild props so DlpService AssemblyName / exe match APP_IDENTIFIERS.
PROPS="$PROJECT_DIR/DlpService/GeneratedIdentity.props"
cat > "$PROPS" <<EOF
<!-- Auto-generated from APP_IDENTIFIERS — do not edit manually -->
<Project>
  <PropertyGroup>
    <AssemblyName>$WINDOWS_SERVICE_NAME</AssemblyName>
  </PropertyGroup>
</Project>
EOF
echo "Generated $PROPS"