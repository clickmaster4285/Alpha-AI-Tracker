package handlers

import (
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/labstack/echo/v4"
)

// ExtensionsHandler serves the self-hosted extension update channel used by
// Chromium's ExtensionInstallForcelist policy (sub-phase 5A).
//
// Store layout (populated by cmd/crxsign):
//
//	<CrxStoreDir>/<extension-id>/<version>.crx
//
// Endpoints (public — browsers fetch these, they cannot carry auth):
//
//	GET /api/v1/extensions/:id/update.xml   → gupdate manifest (Google
//	                                            update2 protocol response)
//	GET /api/v1/extensions/:id/crx          → the newest CRX for that id
type ExtensionsHandler struct {
	storeDir string
	baseURL  string
}

func NewExtensionsHandler(storeDir, publicBaseURL string) *ExtensionsHandler {
	return &ExtensionsHandler{storeDir: storeDir, baseURL: strings.TrimRight(publicBaseURL, "/")}
}

// UpdateManifest serves the <gupdate> XML for a given extension ID.
//
// Example (protocol 2.0, the format Chrome's component/extension updater
// understands):
//
//	<?xml version='1.0' encoding='UTF-8'?>
//	<gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>
//	  <app appid='<ext-id>'>
//	    <updatecheck codebase='<base>/api/v1/extensions/<id>/crx' version='<v>' status='ok'/>
//	  </app>
//	</gupdate>
func (h *ExtensionsHandler) UpdateManifest(c echo.Context) error {
	id := c.Param("id")
	if id == "" {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "extension id required"})
	}

	version, codebase, err := h.latestFor(id)
	if err != nil {
		return c.JSON(http.StatusNotFound, map[string]string{"error": "extension not found"})
	}

	xml := fmt.Sprintf(
		"<?xml version='1.0' encoding='UTF-8'?>\n"+
			"<gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>\n"+
			"  <app appid='%s'>\n"+
			"    <updatecheck codebase='%s' version='%s' status='ok'/>\n"+
			"  </app>\n"+
			"</gupdate>\n",
		id, codebase, version)

	c.Response().Header().Set(echo.HeaderContentType, "application/xml")
	c.Response().Header().Set("Cache-Control", "no-store")
	return c.String(http.StatusOK, xml)
}

// ServeCrx streams the newest CRX for the given extension ID.
func (h *ExtensionsHandler) ServeCrx(c echo.Context) error {
	id := c.Param("id")
	if id == "" {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "extension id required"})
	}

	version, _, err := h.latestFor(id)
	if err != nil {
		return c.JSON(http.StatusNotFound, map[string]string{"error": "extension not found"})
	}

	path := filepath.Join(h.storeDir, id, version+".crx")
	return c.File(path)
}

// latestFor returns the highest version (by filename, assuming X.Y.Z numeric
// ordering) of the CRX for an extension id, plus the absolute codebase URL.
func (h *ExtensionsHandler) latestFor(id string) (version string, codebase string, err error) {
	dir := filepath.Join(h.storeDir, id)
	entries, err := os.ReadDir(dir)
	if err != nil {
		return "", "", fmt.Errorf("no crx for %s", id)
	}

	var versions []string
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		if strings.HasSuffix(name, ".crx") {
			versions = append(versions, strings.TrimSuffix(name, ".crx"))
		}
	}
	if len(versions) == 0 {
		return "", "", fmt.Errorf("no crx for %s", id)
	}
	sort.Slice(versions, func(i, j int) bool { return compareVersions(versions[i], versions[j]) > 0 })
	version = versions[0]

	codebase = fmt.Sprintf("%s/api/v1/extensions/%s/crx", h.baseURL, id)
	return version, codebase, nil
}

// compareVersions compares two dotted-numeric version strings (e.g. 1.0.0 vs
// 1.0.10). Returns >0 when a > b. Non-numeric components are compared as
// strings. This is good enough for an internal update channel.
func compareVersions(a, b string) int {
	as, bs := strings.Split(a, "."), strings.Split(b, ".")
	n := len(as)
	if len(bs) > n {
		n = len(bs)
	}
	for i := 0; i < n; i++ {
		var ai, bi string
		if i < len(as) {
			ai = as[i]
		}
		if i < len(bs) {
			bi = bs[i]
		}
		if ai == bi {
			continue
		}
		aiNum, aErr := numeric(ai)
		biNum, bErr := numeric(bi)
		if aErr == nil && bErr == nil {
			if aiNum < biNum {
				return -1
			}
			if aiNum > biNum {
				return 1
			}
			continue
		}
		if ai < bi {
			return -1
		}
		return 1
	}
	return 0
}

func numeric(s string) (int, error) {
	if s == "" {
		return 0, fmt.Errorf("empty")
	}
	n := 0
	for _, r := range s {
		if r < '0' || r > '9' {
			return 0, fmt.Errorf("not numeric")
		}
		n = n*10 + int(r-'0')
	}
	return n, nil
}
