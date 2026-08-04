// Package crx implements CRX3 packaging for the Alpha AI Tracker Chromium
// extension, matching Chromium's own crx_file implementation byte-for-byte
// (components/crx_file/crx3.proto + crx_creator.cc).
//
// CRX3 binary layout (from crx3.proto):
//
//	[ 4 octets] "Cr24" magic
//	[ 4 octets] format version = 3 (little-endian)
//	[ 4 octets] N = header length (little-endian)
//	[ N octets] header = protobuf CrxFileHeader
//	[ M octets] ZIP archive (the extension payload)
//
// Every proof in CrxFileHeader signs exactly these bytes:
//
//	"CRX3 SignedData\x00" + int32le(len(signed_header_data)) +
//	signed_header_data + archive
//
// where signed_header_data = protobuf SignedData { crx_id = first 16 bytes of
// SHA-256 of the public key (SPKI DER) }, and the signature is
// RSA-PKCS1-SHA256 over that input (kSignatureContext).
package crx

import (
	"archive/zip"
	"bytes"
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/binary"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

// kSignatureContext is the leading bytes of the signed region (15 ASCII
// chars + one 0x00 octet) — must match Chromium exactly.
var kSignatureContext = append([]byte("CRX3 SignedData"), 0x00)

const crxMagic = "Cr24"

// GenerateKey creates a new 2048-bit RSA signing key and returns it as an
// unencrypted PKCS#1 PEM block (the standard Chromium CRX key format).
// Callers MUST persist it with restrictive permissions (see SaveKey) and
// never commit it to the repository — it is the product's extension signing
// secret.
func GenerateKey() (*rsa.PrivateKey, error) {
	return rsa.GenerateKey(rand.Reader, 2048)
}

// SaveKey writes a private key PEM to disk with 0600 permissions,
// refusing to overwrite an existing file.
func SaveKey(path string, key *rsa.PrivateKey) error {
	if _, err := os.Stat(path); err == nil {
		return fmt.Errorf("refusing to overwrite existing key at %s", path)
	} else if !errors.Is(err, os.ErrNotExist) {
		return err
	}
	block := &pem.Block{Type: "RSA PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)}
	data := pem.EncodeToMemory(block)
	if err := os.WriteFile(path, data, 0o600); err != nil {
		return err
	}
	return os.Chmod(path, 0o600)
}

// LoadKey reads a PKCS#1 or PKCS#8 PEM private key from disk.
func LoadKey(path string) (*rsa.PrivateKey, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	block, _ := pem.Decode(data)
	if block == nil {
		return nil, errors.New("no PEM block found in key file")
	}
	if key, err := x509.ParsePKCS1PrivateKey(block.Bytes); err == nil {
		return key, nil
	}
	parsed, err := x509.ParsePKCS8PrivateKey(block.Bytes)
	if err != nil {
		return nil, fmt.Errorf("failed to parse private key: %w", err)
	}
	rsaKey, ok := parsed.(*rsa.PrivateKey)
	if !ok {
		return nil, errors.New("key is not an RSA private key")
	}
	return rsaKey, nil
}

// PublicKeySPKI returns the DER-encoded X.509 SubjectPublicKeyInfo block for
// the key — the exact bytes Chrome hashes to derive the extension ID and
// stores in the CRX header proof.
func PublicKeySPKI(key *rsa.PrivateKey) ([]byte, error) {
	return x509.MarshalPKIXPublicKey(&key.PublicKey)
}

// ExtensionID computes the 32-character a–p extension ID for a CRX-installed
// extension: first 16 bytes of SHA-256 of the SPKI DER, each byte mapped to
// two a–p letters (high nibble, low nibble). Must match Chrome.
func ExtensionID(spki []byte) string {
	sum := sha256.Sum256(spki)
	return nibblesToID(sum[:16])
}

func nibblesToID(hash []byte) string {
	const alphabet = "abcdefghijklmnop"
	var sb strings.Builder
	sb.Grow(32)
	for _, b := range hash {
		sb.WriteByte(alphabet[(b>>4)&0x0F])
		sb.WriteByte(alphabet[b&0x0F])
	}
	return sb.String()
}

// Pack zips an extension directory and signs it into a CRX3 file.
// Returns the CRX bytes, the extension ID (derived from the key), and the
// SHA-256 of the CRX bytes (for the caller to publish as an integrity check).
func Pack(extDir string, key *rsa.PrivateKey) (crxBytes []byte, extID string, crxSHA256 []byte, err error) {
	spki, err := PublicKeySPKI(key)
	if err != nil {
		return nil, "", nil, err
	}

	archive, err := zipExtensionDir(extDir)
	if err != nil {
		return nil, "", nil, fmt.Errorf("zip extension dir: %w", err)
	}

	extID = ExtensionID(spki)

	// signed_header_data = protobuf SignedData { crx_id: spkiSHA256[0:16] }.
	signedHeader := protoFieldBytes(1, extIDBytes(spki)) // field 1 = crx_id bytes
	signedHeaderLen := make([]byte, 4)
	binary.LittleEndian.PutUint32(signedHeaderLen, uint32(len(signedHeader)))

	// Sign "CRX3 SignedData\x00" + int32le(len) + signed_header_data + archive.
	digest := sha256.New()
	digest.Write(kSignatureContext)
	digest.Write(signedHeaderLen)
	digest.Write(signedHeader)
	digest.Write(archive)
	signature, err := rsa.SignPKCS1v15(rand.Reader, key, crypto.SHA256, digest.Sum(nil))
	if err != nil {
		return nil, "", nil, fmt.Errorf("sign: %w", err)
	}

	// AsymmetricKeyProof { public_key: 1, signature: 2 }.
	proof := protoFieldBytes(1, spki)
	proof = append(proof, protoFieldBytes(2, signature)...)

	// CrxFileHeader { sha256_with_rsa: 2 (repeated), signed_header_data: 10000 }.
	header := protoFieldBytes(2, proof)          // sha256_with_rsa
	header = append(header, protoFieldBytes(10000, signedHeader)...) // signed_header_data

	// Assemble CRX3: magic + version(3) + headerLen + header + archive.
	out := new(bytes.Buffer)
	out.WriteString(crxMagic)
	out.Write([]byte{3, 0, 0, 0})
	var headerLen [4]byte
	binary.LittleEndian.PutUint32(headerLen[:], uint32(len(header)))
	out.Write(headerLen[:])
	out.Write(header)
	out.Write(archive)

	crxBytes = out.Bytes()
	sum := sha256.Sum256(crxBytes)
	return crxBytes, extID, sum[:], nil
}

// extIDBytes returns the raw 16-byte crx_id (SHA-256 of SPKI, truncated),
// NOT the a–p display ID — the protobuf field carries the raw bytes.
func extIDBytes(spki []byte) []byte {
	sum := sha256.Sum256(spki)
	return sum[:16]
}

// ─── Minimal protobuf wire encoding (no dependency needed) ───

// protoFieldBytes encodes a length-delimited (wire type 2) field:
// varint tag (field<<3|2) + varint length + payload.
func protoFieldBytes(field int, payload []byte) []byte {
	out := appendVarint(nil, uint64(field)<<3|2)
	out = appendVarint(out, uint64(len(payload)))
	return append(out, payload...)
}

func appendVarint(dst []byte, v uint64) []byte {
	for v >= 0x80 {
		dst = append(dst, byte(v)|0x80)
		v >>= 7
	}
	return append(dst, byte(v))
}

// zipExtensionDir creates an in-memory ZIP of a directory (recursive,
// preserving the extension payload structure). Deterministic file order so
// repackaging the same payload is reproducible.
func zipExtensionDir(dir string) ([]byte, error) {
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)

	var files []string
	err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if info.IsDir() {
			return nil
		}
		files = append(files, path)
		return nil
	})
	if err != nil {
		return nil, err
	}

	for _, path := range files {
		rel, err := filepath.Rel(dir, path)
		if err != nil {
			return nil, err
		}
		rel = filepath.ToSlash(rel)
		w, err := zw.Create(rel)
		if err != nil {
			return nil, err
		}
		f, err := os.Open(path)
		if err != nil {
			return nil, err
		}
		if _, err := io.Copy(w, f); err != nil {
			f.Close()
			return nil, err
		}
		f.Close()
	}
	if err := zw.Close(); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}
