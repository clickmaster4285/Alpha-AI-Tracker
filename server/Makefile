.PHONY: build run dev clean test deps setup-env setup

# ============================================================
# Alpha AI Tracker Server
# Cross-platform Makefile
# Windows + Linux + macOS
# ============================================================

# Detect Windows
ifeq ($(OS),Windows_NT)

PLATFORM := windows
EXE := .exe

else

PLATFORM := unix
EXE :=

endif


# ============================================================
# BUILD
# ============================================================

build:
	@echo Building Alpha AI Tracker server...
	go build -o bin/alpha-ai-server$(EXE) ./cmd/server
	@echo Build completed successfully.


# ============================================================
# RUN
# ============================================================

ifeq ($(PLATFORM),windows)

run: build
	@echo Starting Alpha AI Tracker server...
	.\bin\alpha-ai-server.exe

else

run: build
	@echo Starting Alpha AI Tracker server...
	./bin/alpha-ai-server

endif


# ============================================================
# DEVELOPMENT / HOT RELOAD
# Requires Air:
# go install github.com/air-verse/air@latest
# ============================================================

ifeq ($(PLATFORM),windows)

dev:
	air --build.cmd "go build -o bin/alpha-ai-server.exe ./cmd/server" --build.bin "bin/alpha-ai-server.exe"

else

dev:
	air --build.cmd "go build -o bin/alpha-ai-server ./cmd/server" --build.bin "bin/alpha-ai-server"

endif


# ============================================================
# CLEAN
# ============================================================

ifeq ($(PLATFORM),windows)

clean:
	if exist bin rmdir /s /q bin

else

clean:
	rm -rf bin/

endif


# ============================================================
# TEST
# ============================================================

test:
	go test ./... -v -count=1


# ============================================================
# DEPENDENCIES
# ============================================================

deps:
	go mod tidy
	go mod download


# ============================================================
# ENVIRONMENT
# ============================================================

ifeq ($(PLATFORM),windows)

setup-env:
	if not exist .env copy .env.example .env
	if exist .env echo Environment file ready.

else

setup-env:
	if [ ! -f .env ]; then \
		cp .env.example .env; \
		echo "Created .env from .env.example - edit it with your settings"; \
	else \
		echo ".env already exists."; \
	fi

endif


# ============================================================
# QUICK SETUP
# ============================================================

setup: setup-env deps build
	@echo.
	@echo ==========================================
	@echo Alpha AI Tracker server setup completed.
	@echo ==========================================
	@echo Run 'make run' to start the server.