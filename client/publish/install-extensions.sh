#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────
# Alpha AI Tracker — Native Messaging Host Installer (parity helper)
#
# The GUI (BrowserExtensionService) is the primary installer and writes
# manifests pointing at the C# tracker executable (no Python).
# This script remains for installed-build / CLI parity.
#
# Usage:
#   ./install-extensions.sh                        # Install native host manifests
#   ./install-extensions.sh --update-ids CHROME_ID  # Update Chromium allowed_origins
# ──────────────────────────────────────────────────────────────
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
EXTENSIONS_DIR="$PROJECT_DIR/extensions"
MANIFEST_NAME="com.alphai.tracker.json"
GECKO_ID="alpha-ai-tracker@alphai.com"

# Resolve the tracker binary (C# native host)
resolve_host() {
  local candidates=(
    "$PROJECT_DIR/client"
    "$PROJECT_DIR/alpha-ai-tracker"
    "$SCRIPT_DIR/../client"
    "$(command -v alpha-ai-tracker 2>/dev/null || true)"
  )
  # Prefer the running publish output next to this script when bundled.
  for c in "$SCRIPT_DIR/../client" "$SCRIPT_DIR/client" "${candidates[@]}"; do
    if [ -n "$c" ] && [ -x "$c" ] && [ -f "$c" ]; then
      echo "$(cd "$(dirname "$c")" && pwd)/$(basename "$c")"
      return 0
    fi
  done
  # Fallback: look for a .dll-hosted entry (dotnet client.dll) — not ideal for NM
  # but better than failing silently.
  if [ -f "$PROJECT_DIR/client.dll" ]; then
    echo "dotnet $PROJECT_DIR/client.dll"
    return 0
  fi
  return 1
}

if [[ "${1:-}" == "--update-ids" ]]; then
  CHROME_ID="${2:-}"
  if [ -z "$CHROME_ID" ]; then
    echo "Usage: $0 --update-ids <chromium-extension-id>"
    exit 1
  fi
  ALLOWED_ORIGINS="[\"chrome-extension://${CHROME_ID}/\"]"
  shopt -s nullglob
  for manifest_path in \
    "$HOME"/.config/*/NativeMessagingHosts/"$MANIFEST_NAME" \
    "$HOME"/.mozilla/native-messaging-hosts/"$MANIFEST_NAME"; do
    [ -f "$manifest_path" ] || continue
    # Pure bash JSON rewrite is fragile; use a tiny C#-free sed for the origins array.
    if grep -q '"allowed_origins"' "$manifest_path"; then
      tmp="$(mktemp)"
      awk -v origins="$ALLOWED_ORIGINS" '
        /"allowed_origins"/ {
          print "  \"allowed_origins\": " origins ","
          # skip until closing ]
          while (getline line) { if (line ~ /]/) break }
          next
        }
        { print }
      ' "$manifest_path" > "$tmp" && mv "$tmp" "$manifest_path"
      echo "✓ Updated: $manifest_path"
    fi
  done
  echo "✓ Extension IDs registered. Restart your browser(s)."
  exit 0
fi

NATIVE_HOST_PATH="$(resolve_host)" || {
  echo "Error: tracker executable not found (C# native host)."
  echo "Build/install the client first, then re-run this script."
  exit 1
}
echo "✓ Native host binary: $NATIVE_HOST_PATH"

CHROMIUM_EXT_DIR=""
for d in "$EXTENSIONS_DIR/chromium" "$EXTENSIONS_DIR/chrome"; do
  [ -d "$d" ] && CHROMIUM_EXT_DIR="$d" && break
done

CHROME_EXT_ID=""
if [ -n "$CHROMIUM_EXT_DIR" ] && command -v sha256sum >/dev/null 2>&1; then
  # Mirror ExtensionIdCalculator: SHA-256 of realpath → first 16 bytes → a–p nibbles
  EXT_REAL="$(cd "$CHROMIUM_EXT_DIR" && pwd -P)"
  HASH="$(printf '%s' "$EXT_REAL" | sha256sum | awk '{print $1}')"
  ALPHABET="abcdefghijklmnop"
  CHROME_EXT_ID=""
  for i in $(seq 0 15); do
    BYTE_HEX="${HASH:$((i*2)):2}"
    BYTE_VAL=$((16#$BYTE_HEX))
    HI=$(( (BYTE_VAL >> 4) & 15 ))
    LO=$(( BYTE_VAL & 15 ))
    CHROME_EXT_ID+="${ALPHABET:$HI:1}${ALPHABET:$LO:1}"
  done
  echo "✓ Pre-computed Chromium extension ID: $CHROME_EXT_ID"
fi

ALLOWED_ORIGINS='[]'
if [ -n "$CHROME_EXT_ID" ]; then
  ALLOWED_ORIGINS="[\"chrome-extension://${CHROME_EXT_ID}/\"]"
fi

write_chromium_manifest() {
  local dir="$1"
  mkdir -p "$dir"
  cat > "$dir/$MANIFEST_NAME" << EOF
{
  "name": "com.alphai.tracker",
  "description": "Alpha AI Tracker — Native messaging bridge for browser tab/URL capture",
  "path": "$NATIVE_HOST_PATH",
  "type": "stdio",
  "allowed_origins": $ALLOWED_ORIGINS
}
EOF
  echo "✓ Installed Chromium manifest: $dir"
}

write_gecko_manifest() {
  local dir="$1"
  mkdir -p "$dir"
  cat > "$dir/$MANIFEST_NAME" << EOF
{
  "name": "com.alphai.tracker",
  "description": "Alpha AI Tracker — Native messaging bridge for browser tab/URL capture",
  "path": "$NATIVE_HOST_PATH",
  "type": "stdio",
  "allowed_extensions": ["$GECKO_ID"]
}
EOF
  echo "✓ Installed Gecko manifest: $dir"
}

# Chromium engines: write into every existing ~/.config/*/NativeMessagingHosts-capable root
# that already has a Preferences / Local State profile (structural, not brand-keyed).
if [ -d "$HOME/.config" ]; then
  while IFS= read -r -d '' prefs; do
    root="$(dirname "$(dirname "$prefs")")"
    # Prefer user-data root (Local State sibling) when present
    if [ -f "$root/Local State" ]; then
      write_chromium_manifest "$root/NativeMessagingHosts"
    else
      write_chromium_manifest "$(dirname "$prefs")/../NativeMessagingHosts"
    fi
  done < <(find "$HOME/.config" -maxdepth 3 -type f \( -name Preferences -o -name 'Local State' \) -print0 2>/dev/null | head -z -n 40)
fi

# Always ensure a chromium fallback + gecko host dir exist
write_chromium_manifest "$HOME/.config/chromium/NativeMessagingHosts"
write_gecko_manifest "$HOME/.mozilla/native-messaging-hosts"

echo ""
echo "✓ Native messaging host installed (C# tracker binary)."
echo "  Chromium extension pack: ${CHROMIUM_EXT_DIR:-extensions/chromium}"
echo "  Gecko extension pack:    $EXTENSIONS_DIR/gecko"
echo "  WebKit: not supported"
echo ""
echo "  Prefer the GUI \"Install Extension\" button — it launches the browser and loads the pack."
