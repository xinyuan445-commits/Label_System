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
	"io"
	"math"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

type signedV2 struct {
	Payload   string `json:"payload"`
	Signature string `json:"signature"`
}
type ticketV2 struct {
	DeviceKeyHash string   `json:"deviceKeyHash"`
	Version       int      `json:"version"`
	Scope         string   `json:"scope"`
	JobID         string   `json:"jobId"`
	AttemptID     string   `json:"attemptId"`
	DeviceID      string   `json:"deviceId"`
	Printer       string   `json:"printer"`
	ManifestHash  string   `json:"manifestHash"`
	BundleHash    string   `json:"bundleHash"`
	OutputIDs     []string `json:"outputIds"`
	ExpiresAt     int64    `json:"expiresAt"`
}
type receiptV2 struct {
	ticketV2
	Status string `json:"status"`
}
type pageV2 struct {
	OutputID string `json:"outputId"`
	File     string `json:"file"`
	SHA256   string `json:"sha256"`
}
type manifestV2 struct {
	Version  int      `json:"version"`
	Scope    string   `json:"scope"`
	JobID    string   `json:"jobId"`
	DeviceID string   `json:"deviceId"`
	Printer  string   `json:"printer"`
	WidthMM  float64  `json:"widthMm"`
	HeightMM float64  `json:"heightMm"`
	Pages    []pageV2 `json:"pages"`
}

var uuidV2 = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)
var dispatchV2Driver = dispatchDriverLabels
var v2PrinterExists = func(a *LocalAPI, name string) bool {
	if name == "Microsoft Print to PDF" {
		return true // This explicit destination saves the server-rendered PDF without a physical driver.
	}
	if strings.Contains(strings.ToLower(name), "pdf") {
		return false
	}
	if err := a.local.Refresh(); err != nil {
		return false
	}
	for _, p := range a.local.Snapshot() {
		if p == name {
			return true
		}
	}
	return false
}

func initV2Device(path string) error {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return err
	}
	der, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		return err
	}
	if err = os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return err
	}
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		return err
	}
	_, err = f.Write(der)
	if err == nil {
		err = f.Sync()
	}
	closeErr := f.Close()
	if err != nil {
		return err
	}
	if closeErr != nil {
		return closeErr
	}
	pub, _ := x509.MarshalPKIXPublicKey(&key.PublicKey)
	id := make([]byte, 16)
	if _, err = rand.Read(id); err != nil {
		return err
	}
	id[6] = (id[6] & 15) | 64
	id[8] = (id[8] & 63) | 128
	raw := hex.EncodeToString(id)
	return json.NewEncoder(os.Stdout).Encode(map[string]string{
		"id":   raw[:8] + "-" + raw[8:12] + "-" + raw[12:16] + "-" + raw[16:20] + "-" + raw[20:],
		"name": "打印工作站", "publicKey": base64.StdEncoding.EncodeToString(pub)})
}
func loadV2Key(path string) (*ecdsa.PrivateKey, error) {
	b, e := os.ReadFile(path)
	if e != nil {
		return nil, e
	}
	k, e := x509.ParsePKCS8PrivateKey(b)
	if e != nil {
		return nil, e
	}
	key, ok := k.(*ecdsa.PrivateKey)
	if !ok || key.Curve != elliptic.P256() {
		return nil, errors.New("invalid_device_key")
	}
	return key, nil
}
func v2KeyHash(key *ecdsa.PrivateKey) string {
	public, _ := x509.MarshalPKIXPublicKey(&key.PublicKey)
	hash := sha256.Sum256(public)
	return strings.ToUpper(hex.EncodeToString(hash[:]))
}
func signV2(key *ecdsa.PrivateKey, value any) (signedV2, error) {
	b, e := json.Marshal(value)
	if e != nil {
		return signedV2{}, e
	}
	digest := sha256.Sum256(b)
	signature, e := ecdsa.SignASN1(rand.Reader, key, digest[:])
	return signedV2{base64.StdEncoding.EncodeToString(b), base64.StdEncoding.EncodeToString(signature)}, e
}
func verifyV2(s signedV2, serverKey string) (ticketV2, error) {
	var ticket ticketV2
	b, e := base64.StdEncoding.DecodeString(s.Payload)
	if e != nil || len(b) > 65536 {
		return ticket, errors.New("invalid_ticket")
	}
	pub, e := base64.StdEncoding.DecodeString(serverKey)
	if e != nil {
		return ticket, e
	}
	parsed, e := x509.ParsePKIXPublicKey(pub)
	if e != nil {
		return ticket, e
	}
	key, ok := parsed.(*ecdsa.PublicKey)
	if !ok || key.Curve != elliptic.P256() {
		return ticket, errors.New("invalid_server_key")
	}
	signature, e := base64.StdEncoding.DecodeString(s.Signature)
	if e != nil {
		return ticket, e
	}
	digest := sha256.Sum256(b)
	if !ecdsa.VerifyASN1(key, digest[:], signature) {
		return ticket, errors.New("invalid_signature")
	}
	e = json.Unmarshal(b, &ticket)
	if e != nil || ticket.Version != 2 || !uuidV2.MatchString(ticket.JobID) || !uuidV2.MatchString(ticket.AttemptID) ||
		!uuidV2.MatchString(ticket.DeviceID) || ticket.Scope == "" || len(ticket.Scope) > 1024 || len(ticket.OutputIDs) < 1 || len(ticket.OutputIDs) > 100 {
		return ticket, errors.New("invalid_ticket")
	}
	seen := map[string]bool{}
	for _, id := range ticket.OutputIDs {
		if !uuidV2.MatchString(id) || seen[id] {
			return ticket, errors.New("invalid_output")
		}
		seen[id] = true
	}
	return ticket, nil
}
func (a *LocalAPI) v2Path(scope, job, attempt string) (string, error) {
	if scope == "" || !uuidV2.MatchString(job) || !uuidV2.MatchString(attempt) {
		return "", errors.New("invalid_identity")
	}
	root := a.cfg.V2LedgerDir
	if root == "" {
		cache, e := os.UserCacheDir()
		if e != nil {
			return "", e
		}
		root = filepath.Join(cache, "label-system", "print-v2")
	}
	digest := sha256.Sum256([]byte(scope + "\n" + a.cfg.V2DeviceID))
	dir := filepath.Join(root, hex.EncodeToString(digest[:]))
	if e := os.MkdirAll(dir, 0700); e != nil {
		return "", e
	}
	return filepath.Join(dir, job+"-"+attempt+".json"), nil
}
func persistV2(path string, result signedV2) error {
	b, e := json.Marshal(result)
	if e != nil {
		return e
	}
	f, e := os.OpenFile(path+".tmp", os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0600)
	if e != nil {
		return e
	}
	if _, e = f.Write(b); e == nil {
		e = f.Sync()
	}
	ce := f.Close()
	if e != nil {
		return e
	}
	if ce != nil {
		return ce
	}
	return os.Rename(path+".tmp", path)
}
func (a *LocalAPI) handleV2Status(w http.ResponseWriter, r *http.Request) {
	if r.Method != "GET" {
		http.Error(w, "method", 405)
		return
	}
	parts := strings.Split(strings.TrimPrefix(r.URL.Path, "/v2/label-jobs/"), "/")
	if len(parts) != 2 {
		http.Error(w, "invalid_identity", 400)
		return
	}
	path, e := a.v2Path(r.URL.Query().Get("scope"), parts[0], parts[1])
	if e != nil {
		http.Error(w, "invalid_identity", 400)
		return
	}
	b, e := os.ReadFile(path)
	if e != nil {
		http.Error(w, "result_unknown", 404)
		return
	}
	var result signedV2
	if json.Unmarshal(b, &result) != nil {
		http.Error(w, "result_unknown", 409)
		return
	}
	writeJSON(w, 200, result)
}
func (a *LocalAPI) handleV2Print(w http.ResponseWriter, r *http.Request) {
	if a.cfg.DisablePrinting {
		writeJSON(w, http.StatusConflict, map[string]string{"error": "print_dispatch_disabled"})
		return
	}
	if r.Method != "POST" || !strings.HasPrefix(r.Header.Get("Content-Type"), "application/json") {
		http.Error(w, "json_required", 415)
		return
	}
	var req struct {
		Authorization signedV2 `json:"authorization"`
		Bundle        string   `json:"bundle"`
	}
	if json.NewDecoder(http.MaxBytesReader(w, r.Body, 90<<20)).Decode(&req) != nil {
		http.Error(w, "invalid_request", 400)
		return
	}
	ticket, e := verifyV2(req.Authorization, a.cfg.V2ServerPublicKey)
	if e != nil || ticket.DeviceID != a.cfg.V2DeviceID {
		http.Error(w, "invalid_authorization", 403)
		return
	}
	raw, e := base64.StdEncoding.DecodeString(req.Bundle)
	if e != nil || len(raw) > 64<<20 {
		http.Error(w, "invalid_bundle", 400)
		return
	}
	hash := sha256.Sum256(raw)
	if !strings.EqualFold(hex.EncodeToString(hash[:]), ticket.BundleHash) {
		http.Error(w, "bundle_digest_mismatch", 409)
		return
	}
	manifest, files, e := decodeV2(raw, ticket)
	if e != nil {
		http.Error(w, "invalid_bundle", 422)
		return
	}
	path, e := a.v2Path(ticket.Scope, ticket.JobID, ticket.AttemptID)
	if e != nil {
		http.Error(w, "ledger_unavailable", 503)
		return
	}
	a.labelMu.Lock()
	defer a.labelMu.Unlock()
	if b, readErr := os.ReadFile(path); readErr == nil {
		var old signedV2
		if json.Unmarshal(b, &old) != nil {
			http.Error(w, "result_unknown", 409)
			return
		}
		decoded, _ := base64.StdEncoding.DecodeString(old.Payload)
		var result receiptV2
		if json.Unmarshal(decoded, &result) != nil || result.BundleHash != ticket.BundleHash {
			http.Error(w, "result_unknown", 409)
			return
		}
		writeJSON(w, 200, old)
		return
	} else if !os.IsNotExist(readErr) {
		http.Error(w, "ledger_unavailable", 503)
		return
	}
	if ticket.ExpiresAt < time.Now().Unix() {
		http.Error(w, "authorization_expired", 409)
		return
	}
	lock, e := os.OpenFile(path+".lock", os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if e != nil {
		http.Error(w, "result_unknown", 409)
		return
	}
	lock.Close()
	defer os.Remove(path + ".lock")
	// Another process may have finished between our first read and this lock.
	if b, readErr := os.ReadFile(path); readErr == nil {
		var old signedV2
		if json.Unmarshal(b, &old) != nil {
			http.Error(w, "result_unknown", 409)
			return
		}
		writeJSON(w, 200, old)
		return
	} else if !os.IsNotExist(readErr) {
		http.Error(w, "ledger_unavailable", 503)
		return
	}
	key, e := loadV2Key(a.cfg.V2PrivateKeyPath)
	if e != nil {
		http.Error(w, "device_key_unavailable", 503)
		return
	}
	if v2KeyHash(key) != ticket.DeviceKeyHash {
		http.Error(w, "device_key_mismatch", 403)
		return
	}
	if !v2PrinterExists(a, ticket.Printer) {
		failed, _ := signV2(key, receiptV2{ticket, "not_submitted"})
		if persistV2(path, failed) != nil {
			http.Error(w, "ledger_unavailable", 503)
			return
		}
		writeJSON(w, 200, failed)
		return
	}
	unknown, e := signV2(key, receiptV2{ticket, "unknown"})
	if e != nil || persistV2(path, unknown) != nil {
		http.Error(w, "ledger_unavailable", 503)
		return
	}
	// Crash/error from this point is UNKNOWN: never automatically call the driver again.
	if _, e = dispatchV2Driver(manifest, files); e != nil {
		writeJSON(w, 200, unknown)
		return
	}
	done, e := signV2(key, receiptV2{ticket, "submitted"})
	if e != nil || persistV2(path, done) != nil {
		writeJSON(w, 200, unknown)
		return
	}
	writeJSON(w, 200, done)
}

func (a *LocalAPI) handleV2Device(w http.ResponseWriter, r *http.Request) {
	if r.Method != "GET" {
		http.Error(w, "method", 405)
		return
	}
	key, err := loadV2Key(a.cfg.V2PrivateKeyPath)
	if err != nil || !uuidV2.MatchString(a.cfg.V2DeviceID) {
		http.Error(w, "device_unavailable", 409)
		return
	}
	pub, _ := x509.MarshalPKIXPublicKey(&key.PublicKey)
	writeJSON(w, 200, map[string]string{"id": a.cfg.V2DeviceID, "name": "打印工作站", "publicKey": base64.StdEncoding.EncodeToString(pub)})
}
func decodeV2(raw []byte, ticket ticketV2) (LabelManifest, map[string][]byte, error) {
	var output LabelManifest
	files := map[string][]byte{}
	archive, e := zip.NewReader(bytes.NewReader(raw), int64(len(raw)))
	if e != nil {
		return output, files, e
	}
	if len(archive.File) < 2 || len(archive.File) > 101 {
		return output, files, errors.New("invalid_files")
	}
	total := uint64(0)
	for _, f := range archive.File {
		total += f.UncompressedSize64
		if total > 128<<20 || f.FileInfo().IsDir() || filepath.Base(f.Name) != f.Name || strings.ContainsAny(f.Name, `\/:`) || files[f.Name] != nil {
			return output, files, errors.New("unsafe_file")
		}
		reader, e := f.Open()
		if e != nil {
			return output, files, e
		}
		b, e := io.ReadAll(io.LimitReader(reader, 128<<20+1))
		reader.Close()
		if e != nil || len(b) > 128<<20 {
			return output, files, errors.New("invalid_file")
		}
		files[f.Name] = b
	}
	var manifest manifestV2
	if json.Unmarshal(files["manifest.json"], &manifest) != nil || manifest.Version != 2 || manifest.Scope != ticket.Scope ||
		manifest.JobID != ticket.JobID || manifest.DeviceID != ticket.DeviceID || manifest.Printer != ticket.Printer ||
		math.IsNaN(manifest.WidthMM) || math.IsInf(manifest.WidthMM, 0) || manifest.WidthMM <= 0 || manifest.WidthMM > 1000 ||
		math.IsNaN(manifest.HeightMM) || math.IsInf(manifest.HeightMM, 0) || manifest.HeightMM <= 0 || manifest.HeightMM > 1000 ||
		len(manifest.Pages)+1 != len(files) {
		return output, files, errors.New("manifest_mismatch")
	}
	selected := map[string]bool{}
	for _, id := range ticket.OutputIDs {
		selected[id] = true
	}
	seen := map[string]bool{}
	output = LabelManifest{RunID: strings.ReplaceAll(ticket.JobID, "-", ""), Printer: manifest.Printer, WidthMM: manifest.WidthMM, HeightMM: manifest.HeightMM}
	extension := ".png"
	signature := []byte{137, 80, 78, 71, 13, 10, 26, 10}
	if manifest.Printer == "Microsoft Print to PDF" {
		extension = ".pdf"
		signature = []byte("%PDF-")
	}
	for _, p := range manifest.Pages {
		if !uuidV2.MatchString(p.OutputID) || seen[p.OutputID] || p.File != strings.ReplaceAll(p.OutputID, "-", "")+extension {
			return output, files, errors.New("invalid_page")
		}
		seen[p.OutputID] = true
		b := files[p.File]
		hash := sha256.Sum256(b)
		if !strings.EqualFold(hex.EncodeToString(hash[:]), p.SHA256) || !bytes.HasPrefix(b, signature) {
			return output, files, errors.New("invalid_page")
		}
		if selected[p.OutputID] {
			output.Pages = append(output.Pages, LabelPage{File: p.File, Filename: p.File})
		}
	}
	if len(output.Pages) != len(selected) {
		return output, files, errors.New("missing_output")
	}
	return output, files, nil
}
