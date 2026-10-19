package main

import (
	"archive/zip"
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// These tests replace the driver and printer enumeration. They never print.
func v2Fixture(t *testing.T) (*LocalAPI, ticketV2, signedV2, []byte) {
	t.Helper()
	server, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	device, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	pub, _ := x509.MarshalPKIXPublicKey(&server.PublicKey)
	der, _ := x509.MarshalPKCS8PrivateKey(device)
	root := t.TempDir()
	keyPath := filepath.Join(root, "key.der")
	if e := os.WriteFile(keyPath, der, 0600); e != nil {
		t.Fatal(e)
	}
	job := "11111111-1111-4111-8111-111111111111"
	attempt := "22222222-2222-4222-8222-222222222222"
	output := "33333333-3333-4333-8333-333333333333"
	deviceID := "44444444-4444-4444-8444-444444444444"
	ticket := ticketV2{Version: 2, Scope: "test|888|label", JobID: job, AttemptID: attempt, DeviceID: deviceID,
		DeviceKeyHash: v2KeyHash(device),
		Printer:       "Mock label printer", ManifestHash: "manifest", OutputIDs: []string{output}, ExpiresAt: time.Now().Add(time.Minute).Unix()}
	png := []byte{137, 80, 78, 71, 13, 10, 26, 10, 0}
	digest := sha256.Sum256(png)
	file := "33333333333343338333333333333333.png"
	m := manifestV2{Version: 2, Scope: ticket.Scope, JobID: job, DeviceID: deviceID, Printer: ticket.Printer,
		WidthMM: 80, HeightMM: 50, Pages: []pageV2{{OutputID: output, File: file, SHA256: hex.EncodeToString(digest[:])}}}
	var buf bytes.Buffer
	writer := zip.NewWriter(&buf)
	entry, _ := writer.Create("manifest.json")
	json.NewEncoder(entry).Encode(m)
	entry, _ = writer.Create(file)
	entry.Write(png)
	writer.Close()
	raw := buf.Bytes()
	hash := sha256.Sum256(raw)
	ticket.BundleHash = hex.EncodeToString(hash[:])
	signed, e := signV2(server, ticket)
	if e != nil {
		t.Fatal(e)
	}
	api := &LocalAPI{cfg: &Config{V2DeviceID: deviceID, V2PrivateKeyPath: keyPath,
		V2ServerPublicKey: base64.StdEncoding.EncodeToString(pub), V2LedgerDir: filepath.Join(root, "ledger")}}
	return api, ticket, signed, raw
}

func v2Submit(t *testing.T, a *LocalAPI, s signedV2, bundle []byte) (int, signedV2) {
	t.Helper()
	body, _ := json.Marshal(map[string]any{"authorization": s, "bundle": base64.StdEncoding.EncodeToString(bundle)})
	r := httptest.NewRequest(http.MethodPost, "/v2/print-labels", bytes.NewReader(body))
	r.Header.Set("Content-Type", "application/json")
	w := httptest.NewRecorder()
	a.handleV2Print(w, r)
	var result signedV2
	json.Unmarshal(w.Body.Bytes(), &result)
	return w.Code, result
}
func v2Result(t *testing.T, s signedV2) receiptV2 {
	t.Helper()
	b, e := base64.StdEncoding.DecodeString(s.Payload)
	if e != nil {
		t.Fatal(e)
	}
	var r receiptV2
	if e = json.Unmarshal(b, &r); e != nil {
		t.Fatal(e)
	}
	return r
}
func TestV2LostResponseAndRestartNeverResend(t *testing.T) {
	a, ticket, s, bundle := v2Fixture(t)
	oldDriver, oldExists := dispatchV2Driver, v2PrinterExists
	defer func() { dispatchV2Driver = oldDriver; v2PrinterExists = oldExists }()
	calls := 0
	v2PrinterExists = func(*LocalAPI, string) bool { return true }
	dispatchV2Driver = func(LabelManifest, map[string][]byte) (string, error) { calls++; return "sent", nil }
	code, first := v2Submit(t, a, s, bundle)
	if code != 200 || v2Result(t, first).Status != "submitted" {
		t.Fatalf("first: %d", code)
	}
	// Browser loses the response, then another process reads the same durable ledger.
	restarted := &LocalAPI{cfg: a.cfg}
	code, again := v2Submit(t, restarted, s, bundle)
	if code != 200 || again != first || calls != 1 {
		t.Fatalf("repeat sent %d times", calls)
	}
	w := httptest.NewRecorder()
	restarted.handleV2Status(w, httptest.NewRequest("GET", "/v2/label-jobs/"+ticket.JobID+"/"+ticket.AttemptID+"?scope="+ticket.Scope, nil))
	if w.Code != 200 {
		t.Fatal(w.Code)
	}
}
func TestV2DriverFailureRemainsUnknownAcrossRetries(t *testing.T) {
	a, _, s, bundle := v2Fixture(t)
	oldDriver, oldExists := dispatchV2Driver, v2PrinterExists
	defer func() { dispatchV2Driver = oldDriver; v2PrinterExists = oldExists }()
	calls := 0
	v2PrinterExists = func(*LocalAPI, string) bool { return true }
	dispatchV2Driver = func(LabelManifest, map[string][]byte) (string, error) {
		calls++
		return "", errors.New("ambiguous driver failure")
	}
	_, first := v2Submit(t, a, s, bundle)
	_, second := v2Submit(t, a, s, bundle)
	if v2Result(t, first).Status != "unknown" || first != second || calls != 1 {
		t.Fatal("unknown was resent")
	}
}
func TestV2RejectsTamperedBundleBeforeDriver(t *testing.T) {
	a, _, s, bundle := v2Fixture(t)
	code, _ := v2Submit(t, a, s, append(bundle, 1))
	if code != 409 {
		t.Fatalf("tampered: %d", code)
	}
}
func TestV2PrinterUnavailableIsKnownUnsent(t *testing.T) {
	a, _, s, bundle := v2Fixture(t)
	old := v2PrinterExists
	defer func() { v2PrinterExists = old }()
	v2PrinterExists = func(*LocalAPI, string) bool { return false }
	code, result := v2Submit(t, a, s, bundle)
	if code != 200 || v2Result(t, result).Status != "not_submitted" {
		t.Fatal("unavailable printer is not known unsent")
	}
}
func TestV2WrongDevicePrivateKeyCannotReachDriver(t *testing.T) {
	a, _, s, bundle := v2Fixture(t)
	wrong, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	bytes, _ := x509.MarshalPKCS8PrivateKey(wrong)
	if e := os.WriteFile(a.cfg.V2PrivateKeyPath, bytes, 0600); e != nil {
		t.Fatal(e)
	}
	oldDriver, oldExists := dispatchV2Driver, v2PrinterExists
	defer func() { dispatchV2Driver = oldDriver; v2PrinterExists = oldExists }()
	calls := 0
	v2PrinterExists = func(*LocalAPI, string) bool { return true }
	dispatchV2Driver = func(LabelManifest, map[string][]byte) (string, error) { calls++; return "", nil }
	code, _ := v2Submit(t, a, s, bundle)
	if code != 403 || calls != 0 {
		t.Fatal("mismatched device key reached driver")
	}
}
