# Alpha AI Tracker — Build & Installers

## Prerequisites

### .NET 10 SDK (required for publishing)

```bash
# Ubuntu/Debian
wget https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0
# Add to ~/.bashrc:
export PATH="$HOME/.dotnet:$PATH"
```

### Windows installer (.exe) — only for Linux

```bash
sudo apt install wine wine64

# Download Inno Setup
wget https://jrsoftware.org/download.php/is.exe -O /tmp/innosetup.exe

# Install via Wine
wine /tmp/innosetup.exe /VERYSILENT /SUPPRESSMSGBOXES /DIR="C:\InnoSetup"

# Symlink for the build script
sudo mkdir -p /usr/share/wine
sudo ln -s ~/.wine/drive_c/InnoSetup/ISCC.exe /usr/share/wine/ISCC.exe
```

### Linux installer (.deb)

```bash
sudo apt install dpkg  # pre-installed on Ubuntu/Debian
```

### macOS installer (.dmg) — run ON a Mac

```bash
brew install create-dmg
```

---

## Build Commands

Run **from the `client/` directory**.

```bash
# Build everything available on this machine
bash publish/build-installer.sh

# Build for a specific platform only
bash publish/build-installer.sh -b win     # Windows .exe
bash publish/build-installer.sh -b linux   # Linux .deb
bash publish/build-installer.sh -b mac     # macOS .dmg

# Show help
bash publish/build-installer.sh -h
```

---

## Output Files

| Command      | Output                                          | Format                      |
| ------------ | ----------------------------------------------- | --------------------------- |
| `-b win`   | `installers/AlphaAITracker-Setup-1.0.0.exe`   | Inno Setup wizard installer |
| `-b linux` | `installers/alpha-ai-tracker_1.0.0_amd64.deb` | Debian package              |
| `-b mac`   | `installers/AlphaAITracker.dmg`               | macOS disk image            |

---

## Rebuilding After Code Changes

After making any code changes, you need to rebuild before testing or creating installers.

### Quick Rebuild (Debug)

```bash
cd client
dotnet build
```

### Rebuild and Run Locally

```bash
cd client
dotnet run
```

### Rebuild for Release

```bash
cd client
dotnet build -c Release
```

### Full Rebuild (Clean + Build)

```bash
cd client
dotnet clean
dotnet build
```

### Create Installers After Rebuild

```bash
# Rebuild first
dotnet build -c Release

# Then create installer
bash publish/build-installer.sh
```

---

## Release to GitHub

After building installers, you can publish them as a GitHub Release.

### Prerequisites

```bash
# Install GitHub CLI
sudo apt install gh   # Ubuntu/Debian
# or: brew install gh  # macOS

# Authenticate
echo "YOUR_GITHUB_TOKEN" | gh auth login --with-token
# Or: gh auth login  # interactive browser-based auth
```

### Create a Release (One Command)

Run **from the `client/` directory**.

```bash
# Build all installers, create git tag, and upload to GitHub Releases
bash publish/release.sh v1.0.0

# Or without a version (defaults to v1.0.0)
bash publish/release.sh
```

This will:
1. Build all installers via `build-installer.sh`
2. Commit any pending changes
3. Create and push the git tag (e.g. `v1.0.0`)
4. Create a GitHub Release with the installer files attached
5. Verify the release was created

### Manual Upload

If you prefer to upload manually:

```bash
cd client
bash publish/build-installer.sh
# Then go to https://github.com/AlphaDev-7/Alpha-AI-Tracker/releases/new
# and upload the files from installers/
```

---

## Sending to a Friend

### Windows

Share `installers/AlphaAITracker-Setup-1.0.0.exe` — double-click, Next > Next > Install.

### Linux

Share `installers/alpha-ai-tracker_1.0.0_amd64.deb` — double-click or:

```bash
sudo dpkg -i alpha-ai-tracker_1.0.0_amd64.deb
```

### macOS

Share `installers/AlphaAITracker.dmg` — double-click, drag app to Applications folder.
