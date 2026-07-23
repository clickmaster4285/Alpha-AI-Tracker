.PHONY: build run dev clean test deps

# Build the server binary
build:
	go build -o bin/alpha-ai-server ./cmd/server

# Run the server
run: build
	./bin/alpha-ai-server

# Run with hot-reload (requires air: go install github.com/air-verse/air@latest)
dev:
	air --build.cmd "go build -o bin/alpha-ai-server ./cmd/server" --build.bin "./bin/alpha-ai-server"

# Clean build artifacts
clean:
	rm -rf bin/

# Run tests
test:
	go test ./... -v -count=1

# Download dependencies
deps:
	go mod tidy
	go mod download

# Create a .env from example if it doesn't exist
setup-env:
	@if [ ! -f .env ]; then \
		cp .env.example .env; \
		echo "Created .env from .env.example — edit it with your settings"; \
	fi

# Quick start
setup: setup-env deps build
	@echo "Server built successfully. Run 'make run' to start."
