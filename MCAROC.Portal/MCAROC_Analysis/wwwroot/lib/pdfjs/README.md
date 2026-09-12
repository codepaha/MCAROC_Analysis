# Mozilla PDF.js Vendored Distribution

- **Version**: `v6.3.289`
- **Release Source**: [Mozilla PDF.js Releases v6.3.289](https://github.com/mozilla/pdf.js/releases/tag/v6.3.289)
- **Archive URL**: `https://github.com/mozilla/pdf.js/releases/download/v6.3.289/pdfjs-6.3.289-dist.zip`
- **Archive SHA-256**: `98c5832ffe7af4edd59853476a478c0d4d4d76dd49c1701f4c86f7182725cdf9`
- **License**: Apache License 2.0 (see `LICENSE`)

## Asset Inventory

- `pdf.mjs`: Primary PDF.js library as an ECMAScript module.
- `pdf.worker.mjs`: Web worker for off-thread PDF parsing and rendering.
- `pdf_viewer.css`: Styles for PDF canvas and textLayer rendering.
- `cmaps/`: CMap binary `.bcmap` files for CJK and Unicode character mapping.
- `standard_fonts/`: Standard 14 PostScript font definitions.
- `wasm/`: WebAssembly binary decoders (OpenJPEG, JBIG2, etc.).
- `LICENSE`: Full Apache-2.0 license text.

## Update Procedure

1. Download the official release zip distribution from `https://github.com/mozilla/pdf.js/releases/`.
2. Compute and verify the SHA-256 checksum of the downloaded archive.
3. Extract `build/pdf.mjs`, `build/pdf.worker.mjs`, `web/viewer.css` (saved as `pdf_viewer.css`), `web/cmaps/`, `web/standard_fonts/`, `web/wasm/`, and `LICENSE` into this directory.
4. Update this `README.md` with the new version and SHA-256.
5. Run the client test suite (`node --test MCAROC.Portal/MCAROC_Analysis.Tests/js/pdf-viewer-core.test.js`) and .NET integration test suite.
