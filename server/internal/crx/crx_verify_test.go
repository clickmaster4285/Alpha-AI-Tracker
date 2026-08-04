package crx

import (
	"bytes"
	"crypto"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/binary"
	"os"
	"path/filepath"
	"testing"
)

// TestPackAndVerify packs the real extension payload, then parses the CRX3
// header, extracts the proof, and re-verifies the signature over the exact
// signed region Chromium specifies ("CRX3 SignedData\x00" + int32le(len) +
// signed_header_data + archive).
func TestPackAndVerify(t *testing.T) {
	extDir := filepath.Join("..", "..", "..", "client", "extensions", "chrome")
	if _, err := os.Stat(extDir); err != nil {
		t.Skipf("extension payload not present: %v", err)
	}

	key, err := GenerateKey()
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	crxBytes, extID, crxHash, err := Pack(extDir, key)
	if err != nil {
		t.Fatalf("Pack: %v", err)
	}

	// ── Structural header checks ──
	if string(crxBytes[:4]) != "Cr24" {
		t.Fatalf("bad magic: %q", crxBytes[:4])
	}
	if !(crxBytes[4] == 3 && crxBytes[5] == 0 && crxBytes[6] == 0 && crxBytes[7] == 0) {
		t.Fatalf("bad version bytes: %v", crxBytes[4:8])
	}
	headerLen := binary.LittleEndian.Uint32(crxBytes[8:12])
	header := crxBytes[12 : 12+headerLen]
	archive := crxBytes[12+headerLen:]

	// ZIP archive must start with PK\x03\x04 (local file header).
	if !(len(archive) > 4 && archive[0] == 'P' && archive[1] == 'K' && archive[2] == 3 && archive[3] == 4) {
		t.Fatalf("archive does not look like a zip: % X", archive[:min(4, len(archive))])
	}

	// ── Protobuf parse: sha256_with_rsa (field 2), signed_header_data (10000) ──
	proof, signedHeader := parseHeader(t, header)
	spki := extractField(t, proof, 1)
	sig := extractField(t, proof, 2)

	// crx_id inside signed_header_data (field 1) must be SHA256(spki)[:16].
	sum := sha256.Sum256(spki)
	sid := extractField(t, signedHeader, 1)
	if !bytes.Equal(sid, sum[:16]) {
		t.Fatalf("crx_id mismatch: got %x want %x", sid, sum[:16])
	}

	// Recompute extension ID (a-p) and compare with Pack's output.
	wantID := nibblesToID(sum[:16])
	if wantID != extID {
		t.Fatalf("ext id mismatch: %s vs %s", wantID, extID)
	}

	// ── Verify the RSA-PKCS1-SHA256 signature over the exact signed region ──
	pub, err := x509.ParsePKIXPublicKey(spki)
	if err != nil {
		t.Fatalf("parse spki: %v", err)
	}
	rsaPub, ok := pub.(*rsa.PublicKey)
	if !ok {
		t.Fatalf("spki is not RSA")
	}

	signedHeaderLen := make([]byte, 4)
	binary.LittleEndian.PutUint32(signedHeaderLen, uint32(len(signedHeader)))

	h := sha256.New()
	h.Write(kSignatureContext)
	h.Write(signedHeaderLen)
	h.Write(signedHeader)
	h.Write(archive)
	if err := rsa.VerifyPKCS1v15(rsaPub, crypto.SHA256, h.Sum(nil), sig); err != nil {
		t.Fatalf("signature verification FAILED: %v", err)
	}

	// crxHash must equal SHA-256 of the whole file.
	full := sha256.Sum256(crxBytes)
	if !bytes.Equal(full[:], crxHash) {
		t.Fatalf("crx hash mismatch")
	}

	t.Logf("PASS: magic ok, version=3, header=%d bytes, archive=%d bytes, "+
		"crx_id matches, RSA signature verifies, extID=%s, crxSHA256=%x",
		headerLen, len(archive), extID, crxHash)
}

// parseHeader walks the top-level CrxFileHeader protobuf and returns the
// first sha256_with_rsa message (field 2) and the signed_header_data (10000).
func parseHeader(t *testing.T, data []byte) (proof, signedHeader []byte) {
	t.Helper()
	for len(data) > 0 {
		tag, n := readVarint(data)
		data = data[n:]
		field, wire := int(tag>>3), int(tag&7)
		switch wire {
		case 0:
			_, n = readVarint(data)
			data = data[n:]
		case 2:
			l, m := readVarint(data)
			data = data[m:]
			payload := data[:l]
			data = data[l:]
			if field == 2 {
				proof = payload
			}
			if field == 10000 {
				signedHeader = payload
			}
		default:
			t.Fatalf("unsupported wire type %d", wire)
		}
	}
	return proof, signedHeader
}

func extractField(t *testing.T, msg []byte, wantField int) []byte {
	t.Helper()
	for len(msg) > 0 {
		tag, n := readVarint(msg)
		msg = msg[n:]
		field, wire := int(tag>>3), int(tag&7)
		if wire != 2 {
			continue
		}
		l, m := readVarint(msg)
		msg = msg[m:]
		payload := msg[:l]
		msg = msg[l:]
		if field == wantField {
			return payload
		}
	}
	t.Fatalf("field %d not found", wantField)
	return nil
}

func readVarint(b []byte) (uint64, int) {
	var v uint64
	var shift uint
	for i := 0; i < len(b); i++ {
		v |= uint64(b[i]&0x7f) << shift
		if b[i]&0x80 == 0 {
			return v, i + 1
		}
		shift += 7
	}
	return 0, 0
}
