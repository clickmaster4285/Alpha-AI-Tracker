#!/usr/bin/env bash
# Build installers for Alpha AI Tracker (run from client/)
# Usage: bash publish/build-installer.sh [-b win|linux|mac|all]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
INSTALLER_DIR="$PROJECT_DIR/installers"

BUILD_ALL=true
BUILD_WIN=false
BUILD_LIN=false
BUILD_MAC=false

usage() {
  cat << EOF
Usage: $(basename "$0") [-b <platform>]

Build installers for Alpha AI Tracker.

Options:
  -b <platform>   Build for specific platform: win, linux, mac, all
  -h              Show this help message

Examples:
  $(basename "$0")                 Build all available installers
  $(basename "$0") -b win          Build Windows .exe installer only
  $(basename "$0") -b linux        Build Linux .deb installer only
  $(basename "$0") -b mac          Build macOS .dmg installer only

Prerequisites:
  Windows:  wine + Inno Setup (ISCC.exe)
  Linux:    dpkg-deb
  macOS:    macOS + create-dmg or hdiutil

See build.md for detailed setup instructions.
EOF
  exit 0
}

while getopts "b:h" opt; do
  case $opt in
    b)
      BUILD_ALL=false
      case "$OPTARG" in
        win)   BUILD_WIN=true ;;
        linux) BUILD_LIN=true ;;
        mac)   BUILD_MAC=true ;;
        all)   BUILD_ALL=true ;;
        *)
          echo "Error: Unknown platform '$OPTARG'. Valid: win, linux, mac, all"
          exit 1
          ;;
      esac
      ;;
    h) usage ;;
    *) usage ;;
  esac
done

if [ "$BUILD_ALL" = true ]; then
  BUILD_WIN=true
  BUILD_LIN=true
  BUILD_MAC=true
fi

mkdir -p "$INSTALLER_DIR"

echo "=========================================="
echo " Alpha AI Tracker — Installer Builder"
echo "=========================================="

PUBLISH_WIN="$SCRIPT_DIR/windows"
PUBLISH_LIN="$SCRIPT_DIR/linux"
PUBLISH_MAC="$SCRIPT_DIR/macos"

NEEDS_PUBLISH=false
[ "$BUILD_WIN" = true ] && [ ! -f "$PUBLISH_WIN/client.exe" ] && NEEDS_PUBLISH=true
[ "$BUILD_LIN" = true ] && [ ! -f "$PUBLISH_LIN/client" ]    && NEEDS_PUBLISH=true
[ "$BUILD_MAC" = true ] && [ ! -f "$PUBLISH_MAC/client" ]   && NEEDS_PUBLISH=true

if [ "$NEEDS_PUBLISH" = true ]; then
  echo ""
  echo "[Publish] Publishing .NET app..."
  [ "$BUILD_WIN" = true ] && [ ! -f "$PUBLISH_WIN/client.exe" ] && \
    dotnet publish "$PROJECT_DIR" -c Release -r win-x64 --self-contained -o "$PUBLISH_WIN" 2>/dev/null || \
    echo "  (skipped win-x64, requires .NET SDK)"
  [ "$BUILD_LIN" = true ] && [ ! -f "$PUBLISH_LIN/client" ] && \
    dotnet publish "$PROJECT_DIR" -c Release -r linux-x64 --self-contained -o "$PUBLISH_LIN" 2>/dev/null || \
    echo "  (skipped linux-x64, requires .NET SDK)"
  [ "$BUILD_MAC" = true ] && [ ! -f "$PUBLISH_MAC/client" ] && \
    dotnet publish "$PROJECT_DIR" -c Release -r osx-x64 --self-contained -o "$PUBLISH_MAC" 2>/dev/null || \
    echo "  (skipped osx-x64, requires .NET SDK)"
else
  echo "[Publish] Published output already exists — skipping publish step"
fi

# Windows installer
if [ "$BUILD_WIN" = true ]; then
  echo ""
  echo "[Windows] Building Windows installer..."
  if command -v iscc &>/dev/null; then
    iscc "$SCRIPT_DIR/installer-windows.iss"
  elif command -v wine &>/dev/null && [ -f "/usr/share/wine/ISCC.exe" ]; then
    ISCC_PATH="/usr/share/wine/ISCC.exe"
    ISS_PATH=$(winepath -w "$SCRIPT_DIR/installer-windows.iss" 2>/dev/null || echo "$SCRIPT_DIR/installer-windows.iss")
    wine "$ISCC_PATH" "$ISS_PATH"
  elif command -v wine &>/dev/null && [ -f "$HOME/.wine/drive_c/InnoSetup/ISCC.exe" ]; then
    ISCC_PATH="$HOME/.wine/drive_c/InnoSetup/ISCC.exe"
    ISS_PATH=$(winepath -w "$SCRIPT_DIR/installer-windows.iss" 2>/dev/null || echo "$SCRIPT_DIR/installer-windows.iss")
    wine "$ISCC_PATH" "$ISS_PATH"
  else
    echo "  SKIPPED — requires Inno Setup (iscc)"
    echo "  See build.md for installation instructions"
  fi
fi

# Linux installer
if [ "$BUILD_LIN" = true ]; then
  echo ""
  echo "[Linux] Building .deb installer..."
  if command -v dpkg-deb &>/dev/null; then
    bash "$SCRIPT_DIR/build-deb.sh"
  else
    echo "  SKIPPED — requires dpkg-deb (Debian/Ubuntu)"
  fi
fi

# macOS installer
if [ "$BUILD_MAC" = true ]; then
  echo ""
  echo "[macOS] Building .dmg installer..."
  if [[ "$(uname)" == "Darwin" ]] || command -v create-dmg &>/dev/null; then
    bash "$SCRIPT_DIR/build-dmg.sh"
  else
    echo "  SKIPPED — must be built on macOS (requires hdiutil or create-dmg)"
  fi
fi

echo ""
echo "=========================================="
echo " Installers are in: $INSTALLER_DIR"
echo "=========================================="
ls -lh "$INSTALLER_DIR/" 2>/dev/null || echo "  (no installers were built)"
