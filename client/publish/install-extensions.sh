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

  MANIFEST_NAME="com.alphai.tracker.json"

  # Build allowed_origins dynamically
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

# ─── Generate the Native Messaging manifest ───
MANIFEST_NAME="com.alphai.tracker.json"

cat > /tmp/"$MANIFEST_NAME" << EOF
{
  "name": "com.alphai.tracker",
  "description": "Alpha AI Tracker — Native messaging bridge for browser tab/URL capture",
  "path": "$NATIVE_HOST_PATH",
  "type": "stdio",
  "allowed_origins": []
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
