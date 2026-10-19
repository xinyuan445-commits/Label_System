package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"testing"
)

// Built as a test executable for the explicit SQL acceptance runner. Always replaces both
// printer discovery and dispatch before accepting input. No production binary exposes this.
func TestDevelopmentSqlAcceptanceBridge(t *testing.T) {
	root := os.Getenv("PRINT_ACCEPTANCE_AGENT_ROOT")
	if root == "" {
		t.Skip("explicit existing-development SQL runner only")
	}
	a := &LocalAPI{cfg: &Config{V2DeviceID: os.Getenv("PRINT_ACCEPTANCE_DEVICE"),
		V2PrivateKeyPath: filepath.Join(root, "device.der"), V2ServerPublicKey: os.Getenv("PRINT_ACCEPTANCE_SERVER_PUBLIC"),
		V2LedgerDir: filepath.Join(root, "ledger")}}
	if key := os.Getenv("PRINT_ACCEPTANCE_DEVICE_KEY"); key != "" {
		a.cfg.V2PrivateKeyPath = key
	}
	oldDriver, oldExists := dispatchV2Driver, v2PrinterExists
	defer func() { dispatchV2Driver = oldDriver; v2PrinterExists = oldExists }()
	v2PrinterExists = func(*LocalAPI, string) bool { return true }
	mode := "sent"
	counter := filepath.Join(root, "mock-driver-count.txt")
	calls := func() int { raw, _ := os.ReadFile(counter); n, _ := strconv.Atoi(string(raw)); return n }
	dispatchV2Driver = func(LabelManifest, map[string][]byte) (string, error) {
		if err := os.WriteFile(counter, []byte(strconv.Itoa(calls()+1)), 0600); err != nil {
			return "", err
		}
		if mode == "unknown" {
			return "", errors.New("injected mock uncertainty")
		}
		return "mock accepted", nil
	}
	fmt.Println("READY")
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 4096), 64*1024*1024)
	for scanner.Scan() {
		var q struct {
			Method string          `json:"method"`
			Path   string          `json:"path"`
			Body   json.RawMessage `json:"body"`
			Mode   string          `json:"mode"`
		}
		if err := json.Unmarshal(scanner.Bytes(), &q); err != nil {
			t.Fatal("invalid acceptance command")
		}
		mode = q.Mode
		if q.Method == "COUNT" {
			fmt.Printf("{\"count\":%d}\n", calls())
			continue
		}
		r := httptest.NewRequest(q.Method, q.Path, bytes.NewReader(q.Body))
		r.Header.Set("Content-Type", "application/json")
		w := httptest.NewRecorder()
		if q.Method == "POST" {
			a.handleV2Print(w, r)
		} else {
			a.handleV2Status(w, r)
		}
		result, _ := json.Marshal(map[string]any{"status": w.Code, "body": w.Body.String(), "count": calls()})
		fmt.Println(string(result))
	}
	if scanner.Err() != nil {
		t.Fatal("acceptance input failed")
	}
}
