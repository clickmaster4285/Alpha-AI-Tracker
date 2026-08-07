# Version Management

## Overview

This project uses a **single source of truth** for version numbers: the `VERSION` file at the root of the `client/` directory.

## How It Works

All build and release commands automatically read the version from this file. You **never** need to specify the version on the command line.

### Current Version

```
0.0.1
```

## Usage

### Development

```bash
cd client

# Build and run (version automatically embedded in assembly)
dotnet run

# Build for release (version embedded in assembly metadata)
dotnet build -c Release
```

The version is automatically read from the `VERSION` file and embedded in:
- Assembly version (`AssemblyVersion`)
- File version (`FileVersion`)
- Informational version (`InformationalVersion`)

### Building Installers

```bash
cd client

# Build all installers (Linux, Windows, macOS)
bash publish/build-installer.sh

# Build specific platform
bash publish/build-installer.sh -b linux
bash publish/build-installer.sh -b win
bash publish/build-installer.sh -b mac
```

Each installer will automatically use the version from the `VERSION` file:
- **Linux (.deb):** `alpha-ai-tracker_0.0.1_amd64.deb`
- **Windows (.exe):** `AlphaAITracker-Setup-0.0.1.exe`
- **macOS (.dmg):** `AlphaAITracker.dmg` (with version in app bundle)

### Creating Releases

```bash
cd client

# Create a release (uses version from VERSION file)
bash publish/release.sh

# Optionally override version on command line
bash publish/release.sh v1.2.3
```

The release script will:
1. Read version from `VERSION` file (default: `v0.0.1`)
2. Build all installers with that version
3. Create a git tag with the version
4. Create a GitHub release with the installers

## Changing the Version

To release a new version:

1. Edit the `VERSION` file:
   ```bash
   echo "0.0.2" > client/VERSION
   ```

2. Build and test:
   ```bash
   cd client
   dotnet build -c Release
   ```

3. Create the release:
   ```bash
   bash publish/release.sh
   ```

## Version Format

The version should follow semantic versioning:
```
MAJOR.MINOR.PATCH
```

Examples:
- `0.0.1` - Initial release
- `0.1.0` - New feature
- `1.0.0` - First stable release
- `1.2.3` - Bug fix

## Files That Use the Version

The following files automatically read from the `VERSION` file:

1. **`client.csproj`** - .NET assembly versioning
2. **`publish/build-deb.sh`** - Linux .deb package version
3. **`publish/build-dmg.sh`** - macOS app bundle version (CFBundleVersion)
4. **`publish/installer-windows.iss`** - Windows installer version
5. **`publish/release.sh`** - Git tag and GitHub release version

## Verification

To verify the version is correctly embedded:

```bash
# Check the VERSION file
cat client/VERSION

# Check the built assembly
strings bin/Release/net10.0/client.dll | grep "^0\.0\.1"

# Check a built installer
dpkg-deb -f installers/alpha-ai-tracker_0.0.1_amd64.deb Version
```

## Notes

- The `VERSION` file should contain **only** the version number (no "v" prefix, no extra whitespace)
- The `release.sh` script adds a "v" prefix for git tags (e.g., `v0.0.1`)
- Installer filenames use the version without the "v" prefix (e.g., `0.0.1`)
- Changing the version requires a rebuild: `dotnet clean && dotnet build`
