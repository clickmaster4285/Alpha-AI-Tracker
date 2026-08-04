// Command crxsign signs and packages the Alpha AI Tracker Chromium extension
// into a CRX3 file ready for ExtensionInstallForcelist policy installs.
//
// Usage:
//
//	go run ./cmd/crxsign \
//	    -ext ../../client/extensions/chrome \
//	    -key ~/.config/alpha-ai-tracker/crx-signing-key.pem \
//	    -store ./crx-store
//
// The key is generated on first run (0600 perms) and reused afterwards.
// NEVER commit the key to the repository — it is the product's extension
// signing secret. Store it somewhere secure (e.g. the ops vault) and back it
// up; losing it means the extension ID changes and all installed browsers
// must be re-attached.
//
// Output layout (the server serves this store):
//
//	<store>/<extension-id>/<version>.crx
//
// and the gupdate manifest is generated on demand by the server at
// GET /api/v1/extensions/:id/update.xml.
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"flag"
	"fmt"
	"log"
	"os"
	"path/filepath"

	"github.com/alpha-ai-tracker/server/internal/crx"
)

func main() {
	extDir := flag.String("ext", "", "extension directory to package (e.g. client/extensions/chrome)")
	keyPath := flag.String("key", defaultKeyPath(), "path to the RSA signing key PEM (created if missing)")
	storeDir := flag.String("store", "./crx-store", "server CRX store directory (CRX_STORE_DIR)")
	flag.Parse()

	if *extDir == "" {
		log.Fatal("usage: crxsign -ext <extension-dir> [-key <pem>] [-store <dir>]")
	}

	// 1. Load or create the signing key (secret — 0600, outside the repo).
	var key, err = crx.LoadKey(*keyPath)
	if os.IsNotExist(err) {
		log.Printf("no key at %s — generating a new signing key (0600)", *keyPath)
		key, err = crx.GenerateKey()
		if err != nil {
			log.Fatalf("generate key: %v", err)
		}
		if err := os.MkdirAll(filepath.Dir(*keyPath), 0o700); err != nil {
			log.Fatalf("mkdir key dir: %v", err)
		}
		if err := crx.SaveKey(*keyPath, key); err != nil {
			log.Fatalf("save key: %v", err)
		}
	} else if err != nil {
		log.Fatalf("load key: %v", err)
	}

	spki, err := crx.PublicKeySPKI(key)
	if err != nil {
		log.Fatalf("spki: %v", err)
	}
	extID := crx.ExtensionID(spki)
	log.Printf("extension ID (a-p, from public key): %s", extID)
	log.Printf("public key SPKI (sha256 %s): %s", shortHex(sha256Of(spki)), hex.EncodeToString(spki))

	// 2. Package into CRX3.
	crxBytes, computedID, crxHash, err := crx.Pack(*extDir, key)
	if err != nil {
		log.Fatalf("pack: %v", err)
	}
	if computedID != extID {
		log.Fatalf("internal inconsistency: Pack extID %s != ExtensionID %s", computedID, extID)
	}

	// 3. Write <store>/<id>/<version>.crx (version read from manifest.json).
	version, err := readManifestVersion(*extDir)
	if err != nil {
		log.Fatalf("manifest version: %v", err)
	}

	outDir := filepath.Join(*storeDir, extID)
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		log.Fatalf("mkdir store: %v", err)
	}
	outFile := filepath.Join(outDir, version+".crx")
	if err := os.WriteFile(outFile, crxBytes, 0o644); err != nil {
		log.Fatalf("write crx: %v", err)
	}

	log.Printf("wrote %s (%d bytes, sha256 %s)", outFile, len(crxBytes), hex.EncodeToString(crxHash))
	log.Printf("policy value: %s;https://<server-host>/api/v1/extensions/%s/update.xml", extID, extID)
	log.Printf("KEY: %s — do NOT commit; back it up securely", *keyPath)
}

func defaultKeyPath() string {
	if home, err := os.UserHomeDir(); err == nil {
		return filepath.Join(home, ".config", "alpha-ai-tracker", "crx-signing-key.pem")
	}
	return "./crx-signing-key.pem"
}

func readManifestVersion(extDir string) (string, error) {
	raw, err := os.ReadFile(filepath.Join(extDir, "manifest.json"))
	if err != nil {
		return "", err
	}
	// Minimal JSON parse for "version" — avoid pulling in a dependency for the CLI.
	idx := indexOfKey(raw, "version")
	if idx < 0 {
		return "", fmt.Errorf("no version key in manifest.json")
	}
	start := skipSpace(raw, idx+len("\"version\""))
	if start >= len(raw) || raw[start] != ':' {
		return "", fmt.Errorf("malformed version key")
	}
	start = skipSpace(raw, start+1)
	if start >= len(raw) || raw[start] != '"' {
		return "", fmt.Errorf("version must be a string")
	}
	var b []byte
	for i := start + 1; i < len(raw); i++ {
		if raw[i] == '"' {
			return string(b), nil
		}
		b = append(b, raw[i])
	}
	return "", fmt.Errorf("unterminated version string")
}

func indexOfKey(raw []byte, key string) int {
	target := []byte("\"" + key + "\"")
	for i := 0; i+len(target) <= len(raw); i++ {
		if string(raw[i:i+len(target)]) == string(target) {
			return i
		}
	}
	return -1
}

func skipSpace(raw []byte, i int) int {
	for i < len(raw) && (raw[i] == ' ' || raw[i] == '\t' || raw[i] == '\n' || raw[i] == '\r') {
		i++
	}
	return i
}

func sha256Of(b []byte) []byte {
	sum := sha256.Sum256(b)
	return sum[:]
}

func shortHex(b []byte) string {
	h := hex.EncodeToString(b)
	if len(h) > 16 {
		return h[:16]
	}
	return h
}
