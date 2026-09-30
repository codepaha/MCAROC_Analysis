# Chat retrieval: hybrid search (Full-Text + vector, RRF)

Issue #194. The "Ask Documents" chatbot retrieves evidence from `DocumentChunks` (MCA filings) and
`LitigationOrderChunks` (court orders). Retrieval used to be pure vector search (`VECTOR_DISTANCE`, top-K).
Exact identifiers — case numbers (`TP 255/2019`), section references (`Section 13(2)`), quoted phrases — are
precisely what semantic similarity under-ranks, so retrieval is now hybrid.

## How it works

1. `QuestionHintExtractor` pulls **lexical terms** out of the question: case numbers, statute/section
   references and quoted phrases (max 5). The raw question is never used as a full-text query — a whole
   sentence is mostly noise.
2. The vector search runs exactly as before (soft hints, fail-open fallback, `MaxCosineDistance`).
3. Only if there are lexical terms **and** the table has a full-text index, a `CONTAINSTABLE` search runs
   with each term as an exact phrase, scoped by the same hard filters (RequestId, authoritative batch, SRN).
4. The two rankings are fused with **Reciprocal Rank Fusion** (`score = Σ 1/(60 + rank)`), trimmed to `TopK`.
   RRF uses rank positions only, so cosine distance and FTS `RANK` never need to be on a common scale.

Lexical hits can join the result even past `MaxCosineDistance` (that is the point); they still carry their real
cosine distance as the relevance score. Lexical search only ever *adds* candidates — with no lexical match, the
result is exactly the old semantic result.

Code: `Services/Chat/HybridSearch.cs`, `Services/Chat/DocumentRetriever.cs`,
`Services/Litigation/LitigationDocumentRetriever.cs`, `Services/Chat/QuestionHintExtractor.cs`.

## Prerequisite: SQL Server Full-Text Search

FTS is an optional SQL Server feature. It is **not** in plain SQL Server Express or the stock
`mcr.microsoft.com/mssql/server` Linux image (Linux needs the `mssql-server-fts` package; Windows needs the
"Full-Text and Semantic Extractions for Search" feature).

- Migration `20260930120000_AddChunkFullTextIndexes` creates catalog `ChatChunksCatalog` and full-text indexes
  on both `ChunkText` columns — **only if FTS is installed**; otherwise it is a no-op and chat stays on pure
  vector search (checked per query, no restart needed either way).
- If FTS is installed on a server **after** that migration already ran, create the indexes by hand by running
  the migration's `Up()` SQL (it is idempotent).

Check a server:

```sql
SELECT FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') AS FtsInstalled;
SELECT OBJECT_NAME(object_id) AS TableName FROM sys.fulltext_indexes;  -- expect DocumentChunks, LitigationOrderChunks
```

## Population timing (decision)

The indexes use `CHANGE_TRACKING AUTO`: a newly chunked row becomes lexically searchable shortly after insert,
not at the instant of it. **Accepted as eventually consistent** — the vector path sees new rows immediately and
the lexical path can only add results, so the window can never hide a chunk; at worst a question asked in the
first moments after indexing gets the pre-#194 semantic-only ranking. Tests wait for population by probing for
the exact row (`FullTextTestSupport.WaitUntilIndexedAsync`), never by sleeping.

## Tests

- `HybridSearchTests`, `QuestionHintExtractorTests` — pure unit tests (RRF, query building, term extraction).
- `HybridRetrievalTests`, `LitigationOrderChunkingTests` (hybrid case) — real SQL Server + FTS. They skip where
  FTS is missing, except when `MCAROC_REQUIRE_FULLTEXT=true` (the Linux CI job, whose SQL Server image is built
  from `.github/mssql-fts/Dockerfile`), where a missing FTS install fails them.
