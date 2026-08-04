#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────
# Alpha AI Tracker — Browser Extension Installer
# ──────────────────────────────────────────────────────────────
# This script:
#   1. Locates native-host.py and makes it executable
#   2. Creates the Native Messaging host manifest for Chrome, Chromium, Brave, Firefox
#   3. Prints instructions for loading the extension in the browser
#   4. With --update-ids: updates the native host manifest with extension IDs
#
# Usage:
#   ./install-extensions.sh                        # Install native host + print instructions
#   ./install-extensions.sh --update-ids CHROME_ID FIREFOX_ID   # Update with extension IDs
#   ./install-extensions.sh --update-ids CHROME_ID               # Chrome only
# ──────────────────────────────────────────────────────────────
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
EXTENSIONS_DIR="$PROJECT_DIR/extensions"
MANIFEST_NAME="com.alphai.tracker.json"

# ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ───
# MODE 2: Update native host manifests with extension IDs
# ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ───
if [[ "${1:-}" == "--update-ids" ]]; then
  CHROME_ID="${2:-}"
  FIREFOX_ID="${3:-$CHROME_ID}"

  if [ -z "$CHROME_ID" ]; then
    echo "Usage: $0 --update-ids <chrome-extension-id> [firefox-extension-id]"
    echo ""
    echo "  Get the extension ID from:"
    echo "    Chrome: chrome://extensions (click the extension card)"
    echo "    Firefox: about:debugging#/runtime/this-firefox"
    exit 1
  fi

  # Build allowed_origins dynamically
  # (MANIFEST_NAME is defined near the top of the file.)
  ALLOWED_ORIGINS='['
  if [ -n "$CHROME_ID" ]; then
    ALLOWED_ORIGINS+="\"chrome-extension://${CHROME_ID}/\""
  fi
  if [ -n "$FIREFOX_ID" ] && [ "$FIREFOX_ID" != "$CHROME_ID" ]; then
    [ "$ALLOWED_ORIGINS" != "[" ] && ALLOWED_ORIGINS+=", "
    ALLOWED_ORIGINS+="\"chrome-extension://${FIREFOX_ID}/\""
  elif [ -n "$CHROME_ID" ]; then
    # Firefox also accepts chrome-extension:// ID format for chrome.runtime.connectNative
    ALLOWED_ORIGINS+=", \"chrome-extension://${CHROME_ID}/\""
  fi
  ALLOWED_ORIGINS+=']'

  # Update all installed manifests
  for manifest_path in \
    "$HOME/.config/google-chrome/NativeMessagingHosts/$MANIFEST_NAME" \
    "$HOME/.config/chromium/NativeMessagingHosts/$MANIFEST_NAME" \
    "$HOME/.config/BraveSoftware/Brave-Browser/NativeMessagingHosts/$MANIFEST_NAME" \
    "$HOME/.mozilla/native-messaging-hosts/$MANIFEST_NAME"; do

    if [ -f "$manifest_path" ]; then
      # Use Python for safe JSON manipulation
      python3 -c "
import json
with open('$manifest_path', 'r') as f:
    data = json.load(f)
data['allowed_origins'] = $ALLOWED_ORIGINS
with open('$manifest_path', 'w') as f:
    json.dump(data, f, indent=2)
print('✓ Updated: $manifest_path')
" || echo "⚠ Could not update $manifest_path (python3 required)"
    fi
  done

  echo ""
  echo "✓ Extension IDs registered!"
  echo "  Restart your browser(s) to activate native messaging."
  exit 0
fi

# ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ───
# MODE 1: Install native host manifests + print instructions
# ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ───

# ─── Determine native host script path ───
if [ -f "$EXTENSIONS_DIR/native-host.py" ]; then
    NATIVE_HOST_PATH="$EXTENSIONS_DIR/native-host.py"
elif [ -f "$SCRIPT_DIR/native-host.py" ]; then
    NATIVE_HOST_PATH="$SCRIPT_DIR/native-host.py"
else
    echo "Error: native-host.py not found!"
    echo "Looked in: $EXTENSIONS_DIR"
    echo "Looked in: $SCRIPT_DIR"
    exit 1
fi

chmod +x "$NATIVE_HOST_PATH"
echo "✓ Native host script: $NATIVE_HOST_PATH"

# ─── Detect stale manifests from a previous install ───
# If the user previously installed from a different location (e.g. the dev tree),
# any existing manifest has a `path` pointing somewhere that may no longer exist.
# Warn loudly so they know a reinstall is happening — but always overwrite.
EXISTING_MANIFEST="$HOME/.config/google-chrome/NativeMessagingHosts/$MANIFEST_NAME"
if [ -f "$EXISTING_MANIFEST" ]; then
  EXISTING_PATH="$(python3 -c "
import json, sys
try:
    with open(sys.argv[1]) as f:
        print(json.load(f).get('path', ''))
except: pass
" "$EXISTING_MANIFEST" 2>/dev/null)"
  if [ -n "$EXISTING_PATH" ] && [ ! -f "$EXISTING_PATH" ]; then
    echo ""
    echo "  ⚠ Found stale manifest from a previous install:"
    echo "    path: $EXISTING_PATH"
    echo "    (file no longer exists — Chrome would silently reject native messaging calls)"
    echo "  → Overwriting with current path: $NATIVE_HOST_PATH"
    echo ""
  fi
fi

# ─── Generate the Native Messaging manifest ───
# (MANIFEST_NAME is defined near the top of the file.)

# ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ───
# Pre-compute the Chrome extension ID from the resolved extension path.
# Without this, Chrome silently rejects every native messaging call because
# the extension's actual ID (SHA256 of its filesystem path) won't be in
# allowed_origins. Users would have to run --update-ids separately, which
# is the bug we're fixing here.
# ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ─── ───
CHROME_EXT_DIR="${EXTENSIONS_DIR}/chrome"
CHROME_EXT_ID=""
if [ -d "$CHROME_EXT_DIR" ] && command -v python3 >/dev/null 2>&1; then
  CHROME_EXT_ID="$(python3 -c "
import hashlib, os, sys
ext_path = os.path.realpath(sys.argv[1])
path_hash = hashlib.sha256(ext_path.encode('utf-8')).hexdigest()
alphabet = 'abcdefghijklmnop'
ext_id = ''
for i in range(16):
    byte_val = int(path_hash[i*2:i*2+2], 16)
    ext_id += alphabet[(byte_val >> 4) & 0xf]
    ext_id += alphabet[byte_val & 0xf]
print(ext_id)
" "$CHROME_EXT_DIR" 2>/dev/null)"
fi

# Firefox accepts chrome-extension:// IDs in allowed_extensions (it's just an ID list).
# We register the same ID for both; --update-ids can override later if the user
# loads the extension from a different location.
ALLOWED_ORIGINS='[]'
if [ -n "$CHROME_EXT_ID" ]; then
  ALLOWED_ORIGINS="[\"chrome-extension://${CHROME_EXT_ID}/\"]"
  echo "✓ Pre-computed Chrome extension ID: $CHROME_EXT_ID"
else
  echo "  WARNING: Could not pre-compute Chrome extension ID (python3 missing or chrome/ dir not found)."
  echo "  Manifest will have empty allowed_origins — run: $0 --update-ids YOUR_CHROME_EXTENSION_ID"
fi

cat > /tmp/"$MANIFEST_NAME" << EOF
{
  "name": "com.alphai.tracker",
  "description": "Alpha AI Tracker — Native messaging bridge for browser tab/URL capture",
  "path": "$NATIVE_HOST_PATH",
  "type": "stdio",
  "allowed_origins": $ALLOWED_ORIGINS
}
EOF

# ─── Install for Chrome/Chromium ───
CHROME_HOST_DIR="$HOME/.config/google-chrome/NativeMessagingHosts"
CHROMIUM_HOST_DIR="$HOME/.config/chromium/NativeMessagingHosts"
BRAVE_HOST_DIR="$HOME/.config/BraveSoftware/Brave-Browser/NativeMessagingHosts"

for dir in "$CHROME_HOST_DIR" "$CHROMIUM_HOST_DIR" "$BRAVE_HOST_DIR"; do
    mkdir -p "$dir"
    cp "/tmp/$MANIFEST_NAME" "$dir/$MANIFEST_NAME"
    echo "✓ Installed native host manifest for: $(basename "$(dirname "$(dirname "$dir")")" 2>/dev/null || echo "$dir")"
done

# ─── Install for Firefox ───
FIREFOX_HOST_DIR="$HOME/.mozilla/native-messaging-hosts"
mkdir -p "$FIREFOX_HOST_DIR"
cp "/tmp/$MANIFEST_NAME" "$FIREFOX_HOST_DIR/$MANIFEST_NAME"
echo "✓ Installed native host manifest for Firefox"

rm "/tmp/$MANIFEST_NAME"

# ─── Print extension ID setup instructions ───
echo ""
echo "═══════════════════════════════════════════════════════════════"
echo "  BROWSER EXTENSION SETUP"
echo "═══════════════════════════════════════════════════════════════"
echo ""
echo "  Step 1: Load the extension in your browser"
echo ""
echo "  Chrome/Chromium/Brave:"
echo "    1. Open chrome://extensions"
echo "    2. Enable 'Developer mode' (top right)"
echo "    3. Click 'Load unpacked'"
echo "    4. Select folder: $EXTENSIONS_DIR/chrome"
echo ""
echo "  Firefox:"
echo "    1. Open about:debugging#/runtime/this-firefox"
echo "    2. Click 'Load Temporary Add-on…'"
echo "    3. Select file: $EXTENSIONS_DIR/firefox/manifest.json"
echo "    (For permanent install, see about:addons → Settings → Install Add-on From File)"
echo ""
echo "  Step 2: Get the extension ID"
echo ""
echo "  Chrome: The ID appears on chrome://extensions card"
echo "    → Looks like: abcdefghijklmnopabcdefghijklmn"
echo "  Firefox: The ID appears in about:debugging"
echo ""
echo "  Step 3: Register the extension ID with native messaging"
echo ""
echo "    Run:"
echo "      $0 --update-ids YOUR_CHROME_EXTENSION_ID"
echo ""
echo "    Example:"
echo "      $0 --update-ids abcdefghijklmnopabcdefghijklmn"
echo ""
echo "    Then restart the browser."
echo ""
echo "  To verify: Check the socket file exists after starting tracker:"
echo "    ls -la ~/.local/share/alpha-ai-tracker/native-messaging.sock"
echo ""
echo "═══════════════════════════════════════════════════════════════"
