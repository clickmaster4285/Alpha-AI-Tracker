
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
