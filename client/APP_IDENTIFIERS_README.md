# APP_IDENTIFIERS — App Identity Management

> **Single source of truth for all package/app identifiers across the project.**

## Overview

`APP_IDENTIFIERS` centralizes every piece of app identity metadata that used to be scattered and hardcoded across build scripts, installer templates, and platform configs.

All build/release tooling now reads from this file:

- `publish/build-deb.sh`
- `publish/build-dmg.sh`
- `publish/generate-windows-vars.sh`
- `publish/installer-windows.iss` (via `windows_vars.iss`)
- `publish/release.sh`

## Editing Identifiers

To rename the app, change its bundle ID, or re-brand for a customer:

```bash
nano client/APP_IDENTIFIERS
```

That’s it. Do **not** edit the individual build scripts or `.iss` file directly.

## Supported Variables

| Variable | Example | Used In |
|----------|---------|---------|
| `PACKAGE_NAME` | `alpha-ai-tracker` | Linux `.deb` package, install path, systemd unit paths |
| `DISPLAY_NAME` | `Alpha AI Tracker` | Desktop entries, installer UI, release notes |
| `BUNDLE_ID` | `com.alphaai.tracker` | macOS `Info.plist` (`CFBundleIdentifier`) |
| `EXECUTABLE_NAME` | `client` | Binary name, launch commands |
| `PUBLISHER` | `Alpha AI` | `.deb` Maintainer, Windows installer publisher |
| `APP_MUTEX` | `AlphaAITracker` | Windows single-instance mutex |
| `WINDOWS_INSTALLER_NAME` | `AlphaAITracker` | Windows `.exe` installer filename |
| `DEB_PACKAGE_NAME` | `alpha-ai-tracker` | Linux `.deb` package filename |
| `MACOS_BUNDLE_NAME` | `Alpha AI Tracker` | macOS `.app` bundle and `.dmg` volume name |
| `APP_URL` | `https://alpha-ai-tracker.example.com` | Installer support/update URLs |
| `DESKTOP_CATEGORIES` | `Office;Productivity;` | Linux `.desktop` file `Categories=` |
| `WM_CLASS` | `AlphaAITracker` | Linux window manager class |

## How It Works

### Linux (`build-deb.sh`)

```bash
source "$PROJECT_DIR/APP_IDENTIFIERS"
```

Then `$PACKAGE_NAME`, `$DISPLAY_NAME`, etc. are used everywhere:

```bash
cat > "$PKG_ROOT/usr/share/applications/$PACKAGE_NAME.desktop" << EOF
[Desktop Entry]
Name=$DISPLAY_NAME
Exec=/usr/share/$PACKAGE_NAME/$EXECUTABLE_NAME
EOF
```

### macOS (`build-dmg.sh`)

```bash
source "$PROJECT_DIR/APP_IDENTIFIERS"
```

Used in `Info.plist`:

```xml
<key>CFBundleIdentifier</key>
<string>$BUNDLE_ID</string>
<key>CFBundleName</string>
<string>$DISPLAY_NAME</string>
```

### Windows (`installer-windows.iss`)

Because Inno Setup `.iss` scripts cannot `source` shell files, the release/build pipeline generates a temporary include file:

```bash
bash publish/generate-windows-vars.sh
# produces publish/windows/windows_vars.iss
```

Which the `.iss` file includes:

```iss
#include "windows\windows_vars.iss"
```

The `.iss` then uses `{#MyAppName}`, `{#MyAppExeName}`, etc. throughout.

### GitHub Releases (`release.sh`)

```bash
source "$PROJECT_DIR/APP_IDENTIFIERS"
```

Used in release notes and `gh` title:

```bash
gh release create "$VERSION" --title "$DISPLAY_NAME $VERSION"
```

## Adding a New Identifier

1. Add it to `client/APP_IDENTIFIERS` in `KEY="value"` format.
2. Use it in the relevant build script(s).
3. Document it in this README.
4. If it’s Windows-specific, also add it to `generate-windows-vars.sh`.

## Example: Rebranding for a Customer

```bash
cat > client/APP_IDENTIFIERS <<EOF
PACKAGE_NAME="mycompany-tracker"
DISPLAY_NAME="MyCompany Tracker"
BUNDLE_ID="com.mycompany.tracker"
EXECUTABLE_NAME="tracker"
PUBLISHER="MyCompany Inc"
...
EOF
```

Then run builds as normal — every installer, desktop entry, and bundle metadata updates automatically.

## Related Files

- `client/VERSION` — version number
- `client/APP_IDENTIFIERS` — app identity
- `client/VERSION_README.md` — version management docs
