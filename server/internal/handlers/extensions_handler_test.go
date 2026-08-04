package handlers

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/alpha-ai-tracker/server/internal/crx"
	"github.com/labstack/echo/v4"
)

// TestUpdateManifestEndToEnd packs a real CRX into a temp store, serves it via
// the real handler over real HTTP (through the same Echo wiring the router
// uses), and verifies:
//  1. update.xml returns a valid gupdate manifest with the correct appid,
//     version and codebase URL.
//  2. GET /crx streams the same bytes that were written (hash match).
func TestUpdateManifestEndToEnd(t *testing.T) {
	// Real extension payload.
	extDir := filepath.Join("..", "..", "..", "client", "extensions", "chrome")
	if _, err := os.Stat(extDir); err != nil {
		t.Skipf("extension payload not present: %v", err)
	}

	// Pack a real CRX into a temp store dir: <store>/<id>/1.0.0.crx
	key, err := crx.GenerateKey()
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	crxBytes, extID, crxHash, err := crx.Pack(extDir, key)
	if err != nil {
		t.Fatalf("Pack: %v", err)
	}

	storeDir := t.TempDir()
	version := "1.0.0"
	outDir := filepath.Join(storeDir, extID)
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(outDir, version+".crx"), crxBytes, 0o644); err != nil {
		t.Fatal(err)
	}

	handler := NewExtensionsHandler(storeDir, "http://localhost:8080")

	// Wire exactly like router.Setup does.
	e := echo.New()
	e.GET("/api/v1/extensions/:id/update.xml", handler.UpdateManifest)
	e.GET("/api/v1/extensions/:id/crx", handler.ServeCrx)

	srv := httptest.NewServer(e)
	defer srv.Close()

	// ── 1. Fetch update.xml ──
	resp, err := http.Get(srv.URL + "/api/v1/extensions/" + extID + "/update.xml")
	if err != nil {
		t.Fatalf("GET update.xml: %v", err)
	}
	body, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatalf("update.xml status %d", resp.StatusCode)
	}
	xml := string(body)
	if !strings.Contains(xml, "<gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>") {
		t.Fatalf("not a gupdate manifest:\n%s", xml)
	}
	wantAppID := fmt.Sprintf("<app appid='%s'>", extID)
	if !strings.Contains(xml, wantAppID) {
		t.Fatalf("appid missing:\n%s", xml)
	}
	wantCodebase := fmt.Sprintf("codebase='http://localhost:8080/api/v1/extensions/%s/crx'", extID)
	if !strings.Contains(xml, wantCodebase) {
		t.Fatalf("codebase wrong:\n%s", xml)
	}
	if !strings.Contains(xml, "version='1.0.0'") {
		t.Fatalf("version missing:\n%s", xml)
	}
	t.Logf("update.xml OK:\n%s", xml)

	// ── 2. Fetch the CRX and verify bytes + hash ──
	resp2, err := http.Get(srv.URL + "/api/v1/extensions/" + extID + "/crx")
	if err != nil {
		t.Fatalf("GET crx: %v", err)
	}
	crxBody, _ := io.ReadAll(resp2.Body)
	resp2.Body.Close()
	if resp2.StatusCode != 200 {
		t.Fatalf("crx status %d", resp2.StatusCode)
	}
	if len(crxBody) != len(crxBytes) {
		t.Fatalf("crx size mismatch: %d vs %d", len(crxBody), len(crxBytes))
	}
	sum := sha256.Sum256(crxBody)
	if hex.EncodeToString(sum[:]) != hex.EncodeToString(crxHash) {
		t.Fatalf("served crx hash mismatch")
	}
	if string(crxBody[:4]) != "Cr24" {
		t.Fatalf("served file not a crx")
	}
	t.Logf("served crx OK: %d bytes, sha256 %x", len(crxBody), sum)
}
