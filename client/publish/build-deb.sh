#!/usr/bin/env bash
# Build a .deb package for Alpha AI Tracker (Linux)
# Output: ../installers/alpha-ai-tracker_1.0.0_amd64.deb
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
APP_NAME="alpha-ai-tracker"
VERSION="$(cat "$PROJECT_DIR/VERSION")"
ARCH="amd64"
INSTALLER_DIR="$PROJECT_DIR/installers"
PKG_ROOT="/tmp/${APP_NAME}_deb"

rm -rf "$PKG_ROOT"
mkdir -p "$PKG_ROOT/DEBIAN"
mkdir -p "$PKG_ROOT/usr/share/$APP_NAME"
mkdir -p "$PKG_ROOT/usr/share/applications"
mkdir -p "$PKG_ROOT/usr/bin"
mkdir -p "$PKG_ROOT/usr/share/icons/hicolor/256x256/apps"
# /etc/alpha-ai-tracker no longer needed — config.enc is self-contained in app dir

# Copy published binaries
cp -r "$SCRIPT_DIR/linux/"* "$PKG_ROOT/usr/share/$APP_NAME/"

# Copy config.enc (encrypted .env) if it exists
if [ -f "$SCRIPT_DIR/linux/config.enc" ]; then
  cp "$SCRIPT_DIR/linux/config.enc" "$PKG_ROOT/usr/share/$APP_NAME/"
  echo "  Bundled config.enc"
else
  echo "  WARNING: config.enc not found! The app will need other config sources."
fi

# Placeholder for platform-specific files (AT-SPI now inline in C#)

# Desktop entry
cat > "$PKG_ROOT/usr/share/applications/$APP_NAME.desktop" << EOF
[Desktop Entry]
Name=Alpha AI Tracker
Comment=Employee Monitoring & Productivity Dashboard
Exec=/usr/share/$APP_NAME/client
Icon=$APP_NAME
Terminal=false
Type=Application
Categories=Office;Productivity;
StartupWMClass=AlphaAITracker
EOF

# Symlink in PATH
ln -s "/usr/share/$APP_NAME/client" "$PKG_ROOT/usr/bin/$APP_NAME"

# Icon (use a placeholder if none exists)
if [ -f "$PROJECT_DIR/Assets/icon.png" ]; then
  cp "$PROJECT_DIR/Assets/icon.png" "$PKG_ROOT/usr/share/icons/hicolor/256x256/apps/$APP_NAME.png"
else
  # Generate a minimal placeholder icon
  convert -size 256x256 xc:transparent "$PKG_ROOT/usr/share/icons/hicolor/256x256/apps/$APP_NAME.png" 2>/dev/null || true
fi

# Control file
cat > "$PKG_ROOT/DEBIAN/control" << EOF
Package: $APP_NAME
Version: $VERSION
Section: utils
Priority: optional
Architecture: $ARCH
Maintainer: Alpha AI <support@example.com>
Description: Alpha AI Tracker
 Employee Monitoring & Productivity Dashboard.
 Installed to /usr/share/$APP_NAME with a desktop entry and PATH symlink.
EOF

# postinst script
cat > "$PKG_ROOT/DEBIAN/postinst" << 'EOF'
#!/bin/sh
set -e

# Enable AT-SPI accessibility (required for Wayland window title tracking)
if command -v gsettings >/dev/null 2>&1; then
  for user_home in /home/*; do
    user=$(basename "$user_home")
    if [ -d "$user_home" ]; then
      su "$user" -c "gsettings set org.gnome.desktop.interface toolkit-accessibility true" 2>/dev/null || true
    fi
  done
fi

# Install xdotool (X11 fallback for window title detection)
if command -v apt-get >/dev/null 2>&1; then
  apt-get install -y xdotool 2>/dev/null || true
fi

# Update desktop database
if [ -x /usr/bin/update-desktop-database ]; then
  update-desktop-database 2>/dev/null || true
fi
if [ -x /usr/bin/gtk-update-icon-cache ]; then
  gtk-update-icon-cache -f /usr/share/icons/hicolor 2>/dev/null || true
fi
EOF
chmod +x "$PKG_ROOT/DEBIAN/postinst"

# prerm script — kill running instance before uninstall
cat > "$PKG_ROOT/DEBIAN/prerm" << 'EOF'
#!/bin/sh
set -e

# Forcefully kill any running Alpha AI Tracker instance
APP_PID=$(pidof alpha-ai-tracker 2>/dev/null || pidof client 2>/dev/null || echo "")
if [ -n "$APP_PID" ]; then
  echo "Stopping Alpha AI Tracker (PID: $APP_PID)..."
  kill -9 $APP_PID 2>/dev/null || true
  sleep 1
fi
EOF
chmod +x "$PKG_ROOT/DEBIAN/prerm"

mkdir -p "$INSTALLER_DIR"
dpkg-deb --build --root-owner-group "$PKG_ROOT" "$INSTALLER_DIR/${APP_NAME}_${VERSION}_${ARCH}.deb"

echo "Linux .deb installer created: $INSTALLER_DIR/${APP_NAME}_${VERSION}_${ARCH}.deb"
