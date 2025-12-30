// Test vector generator for PatchSync FastCDC compatibility testing.
// Generates deterministic test data and chunks it using fastcdc-go,
// outputting JSON test vectors that can be verified against the C# implementation.
//
// Usage: go run generate.go > testvectors.json
package main

import (
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"math/rand"
	"os"

	"github.com/jotfs/fastcdc-go"
)

type ChunkVector struct {
	Offset int    `json:"offset"`
	Length int    `json:"length"`
	Hash   string `json:"hash"` // SHA256 hex
}

type TestCase struct {
	Name        string        `json:"name"`
	Description string        `json:"description"`
	Seed        int64         `json:"seed"`
	DataSize    int           `json:"dataSize"`
	DataBase64  string        `json:"dataBase64"` // Actual test data to avoid PRNG differences
	MinSize     int           `json:"minSize"`
	AverageSize int           `json:"averageSize"`
	MaxSize     int           `json:"maxSize"`
	Chunks      []ChunkVector `json:"chunks"`
}

type TestVectors struct {
	Version    string     `json:"version"`
	Generator  string     `json:"generator"`
	TestCases  []TestCase `json:"testCases"`
}

func main() {
	vectors := TestVectors{
		Version:   "1.0",
		Generator: "fastcdc-go",
		TestCases: []TestCase{},
	}

	// Test case 1: Small file with default options
	vectors.TestCases = append(vectors.TestCases, generateTestCase(
		"small_default",
		"Small file (64KB) with default chunk sizes",
		42,
		64*1024,
		4*1024,
		16*1024,
		64*1024,
	))

	// Test case 2: Medium file
	vectors.TestCases = append(vectors.TestCases, generateTestCase(
		"medium_default",
		"Medium file (1MB) with default chunk sizes",
		123,
		1024*1024,
		4*1024,
		16*1024,
		64*1024,
	))

	// Test case 3: File smaller than min size
	vectors.TestCases = append(vectors.TestCases, generateTestCase(
		"tiny",
		"File smaller than minimum chunk size",
		999,
		2*1024,
		4*1024,
		16*1024,
		64*1024,
	))

	// Test case 4: Large chunks
	vectors.TestCases = append(vectors.TestCases, generateTestCase(
		"large_chunks",
		"File with larger chunk sizes",
		456,
		512*1024,
		16*1024,
		64*1024,
		256*1024,
	))

	// Test case 5: File exactly at chunk boundaries
	vectors.TestCases = append(vectors.TestCases, generateTestCase(
		"boundary_aligned",
		"File size aligned to average chunk size",
		789,
		16*1024*4,
		4*1024,
		16*1024,
		64*1024,
	))

	// Test case 6: Highly compressible data (zeros)
	vectors.TestCases = append(vectors.TestCases, generateTestCaseZeros(
		"zeros",
		"All zeros - tests boundary behavior on uniform data",
		128*1024,
		4*1024,
		16*1024,
		64*1024,
	))

	output, err := json.MarshalIndent(vectors, "", "  ")
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error marshaling JSON: %v\n", err)
		os.Exit(1)
	}

	fmt.Println(string(output))
}

func generateTestCase(name, desc string, seed int64, dataSize, minSize, avgSize, maxSize int) TestCase {
	// Generate deterministic random data
	rng := rand.New(rand.NewSource(seed))
	data := make([]byte, dataSize)
	rng.Read(data)

	return generateTestCaseFromData(name, desc, seed, data, minSize, avgSize, maxSize)
}

func generateTestCaseZeros(name, desc string, dataSize, minSize, avgSize, maxSize int) TestCase {
	data := make([]byte, dataSize)
	// All zeros by default
	return generateTestCaseFromData(name, desc, 0, data, minSize, avgSize, maxSize)
}

func generateTestCaseFromData(name, desc string, seed int64, data []byte, minSize, avgSize, maxSize int) TestCase {
	opts := fastcdc.Options{
		MinSize:     minSize,
		AverageSize: avgSize,
		MaxSize:     maxSize,
	}

	reader := &bytesReader{data: data}
	chunker, err := fastcdc.NewChunker(reader, opts)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error creating chunker: %v\n", err)
		os.Exit(1)
	}

	var chunks []ChunkVector

	for {
		chunk, err := chunker.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error chunking: %v\n", err)
			os.Exit(1)
		}

		hash := sha256.Sum256(chunk.Data)
		chunks = append(chunks, ChunkVector{
			Offset: chunk.Offset,
			Length: chunk.Length,
			Hash:   hex.EncodeToString(hash[:]),
		})
	}

	return TestCase{
		Name:        name,
		Description: desc,
		Seed:        seed,
		DataSize:    len(data),
		DataBase64:  base64.StdEncoding.EncodeToString(data),
		MinSize:     minSize,
		AverageSize: avgSize,
		MaxSize:     maxSize,
		Chunks:      chunks,
	}
}

// bytesReader wraps a byte slice to implement io.Reader
type bytesReader struct {
	data   []byte
	offset int
}

func (r *bytesReader) Read(p []byte) (n int, err error) {
	if r.offset >= len(r.data) {
		return 0, io.EOF
	}
	n = copy(p, r.data[r.offset:])
	r.offset += n
	return n, nil
}
