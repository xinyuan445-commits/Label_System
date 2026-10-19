package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestDisabledPrintingBlocksBothDispatchPaths(t *testing.T) {
	cfg, err := LoadConfig(writeConfig(t, "client_only: true\nserver_url: http://127.0.0.1:18003\ndisable_printing: true\n"))
	if err != nil || !cfg.DisablePrinting {
		t.Fatalf("read-only config: %v", err)
	}
	api := NewLocalAPI(cfg, &LocalPrinters{})
	for _, path := range []string{"/print", "/print-labels", "/v2/print-labels"} {
		result := httptest.NewRecorder()
		api.Handler().ServeHTTP(result, httptest.NewRequest(http.MethodPost, path, strings.NewReader("invalid payload")))
		if result.Code != http.StatusConflict || !strings.Contains(result.Body.String(), "print_dispatch_disabled") {
			t.Fatalf("dispatch gate %s: %d %s", path, result.Code, result.Body.String())
		}
	}
	for _, path := range []string{"/status", "/printers"} {
		result := httptest.NewRecorder()
		api.Handler().ServeHTTP(result, httptest.NewRequest(http.MethodGet, path, nil))
		if result.Code != http.StatusOK {
			t.Fatalf("read path %s: %d", path, result.Code)
		}
		if path == "/status" {
			var body map[string]any
			if err := json.Unmarshal(result.Body.Bytes(), &body); err != nil || body["print_dispatch_enabled"] != false {
				t.Fatalf("disabled status: %s", result.Body.String())
			}
		}
	}
}
