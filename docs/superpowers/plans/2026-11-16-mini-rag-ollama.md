# Mini RAG with Ollama Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A local RAG app: upload PDF/DOCX/TXT → chunk → embed with Ollama → store in Qdrant → answer questions with qwen3:8b and citations.

**Architecture:** ASP.NET Core (.NET 10) controllers over small single-purpose services that call Ollama and Qdrant REST APIs with typed `HttpClient`s. Indexing runs as a background task with in-memory status rebuilt from Qdrant at startup. Angular standalone single page proxies `/api` to the backend.

**Tech Stack:** .NET 10, ASP.NET Core, PdfPig, DocumentFormat.OpenXml, xUnit; Angular 22; Qdrant (Docker); Ollama (host).

**Spec:** `docs/superpowers/specs/2026-11-16-mini-rag-ollama-design.md`

## Global Constraints

- Target framework `net10.0`. Execution: one agent at a time (user rule) → native execution.
- No Gemini, Redis, RabbitMQ, Hangfire, SignalR, S3, authentication.
- Defaults: Ollama `http://localhost:11434`, chat `qwen3:8b`, embed `nomic-embed-text`, Qdrant `http://localhost:6333`, chunk 700 tokens, overlap 100, TopK 5.
- Never pull models automatically; on missing model, log/return the `ollama pull <model>` hint.
- Embedding dimension is detected at startup, never hard-coded.
- Insufficient-information sentence (exact): `The uploaded documents do not contain sufficient information to answer this question.`

## Review Focus

1. Scanned/image-only PDF or empty file → no text extracted → document status `Failed` with message "No extractable text", not an empty `Indexed` doc. (Task 2 test `Extract_EmptyTxt_ReturnsNoPages`, Task 4 check.)
2. Unsupported extension (e.g. `.xlsx`) → HTTP 400 with clear message, nothing stored. (Task 5 e2e check.)
3. Qdrant collection exists with a different dimension (model swapped) → startup fails with clear message naming both dimensions. (Task 3.)
4. Question when no documents are indexed → insufficient-information answer, empty citations, no LLM call. (Task 4 `RagService` path; Task 7 e2e.)
5. Chunk overlap ≥ chunk size in config → chunker throws `ArgumentException` at construction rather than looping forever. (Task 2 test `Ctor_OverlapNotLessThanSize_Throws`.)

---

### Task 1: Scaffolding, configuration, Qdrant compose

**Files:**
- Create: `MiniRag.slnx`, `backend/MiniRag.Api/MiniRag.Api.csproj`, `backend/MiniRag.Api/Program.cs`, `backend/MiniRag.Api/appsettings.json`, `backend/MiniRag.Api/Options/{OllamaOptions,QdrantOptions,RagOptions}.cs`, `backend/MiniRag.Tests/MiniRag.Tests.csproj`, `docker-compose.yml`

**Interfaces:**
- Produces: `OllamaOptions { BaseUrl, ChatModel, EmbeddingModel, EnableThinking=false, DocumentPrefix="search_document: ", QueryPrefix="search_query: ", TimeoutSeconds=300 }`; `QdrantOptions { Url, Collection="documents" }`; `RagOptions { ChunkSizeTokens=700, ChunkOverlapTokens=100, TopK=5, MinScore=0.45, MaxUploadMb=50, UploadPath="uploads" }`; config sections `Ollama`, `Qdrant`, `Rag`.

- [ ] Step 1: `dotnet new webapi --use-controllers -f net10.0` and `dotnet new xunit`, add to `MiniRag.slnx`, test project references API. Packages: `UglyToad.PdfPig`, `DocumentFormat.OpenXml`.
- [ ] Step 2: Options classes bound in `Program.cs`; CORS policy allowing `http://localhost:4200`.
- [ ] Step 3: `docker-compose.yml`: service `qdrant`, image `qdrant/qdrant:v1.15.0` (or newest available), ports `6333:6333`, `6334:6334`, volume `./qdrant_storage:/qdrant/storage`.
- [ ] Step 4: Verify `dotnet build` succeeds and `docker compose up -d` → `curl localhost:6333` returns JSON with `version`.
- [ ] Step 5: Commit `chore: scaffold API, tests and Qdrant compose`.

### Task 2: Text extraction and chunking (TDD)

**Files:**
- Create: `backend/MiniRag.Api/Services/DocumentTextExtractor.cs`, `backend/MiniRag.Api/Services/TextChunker.cs`, `backend/MiniRag.Api/Models/TextModels.cs`
- Test: `backend/MiniRag.Tests/TextChunkerTests.cs`, `backend/MiniRag.Tests/DocumentTextExtractorTests.cs`

**Interfaces:**
- Produces: `record PageText(int? PageNumber, string Text)`; `record TextChunk(int ChunkIndex, int? PageNumber, string Text)`;
  `DocumentTextExtractor.Extract(Stream content, string fileName) : IReadOnlyList<PageText>` (throws `NotSupportedException` for other extensions; static `IsSupported(string fileName) : bool`, supported `.pdf .docx .txt`);
  `TextChunker(int chunkSizeTokens, int overlapTokens)`, `Chunk(IReadOnlyList<PageText> pages) : IReadOnlyList<TextChunk>`; approximation 4 chars per token.

- [ ] Step 1: Failing tests:
  - `Chunk_ShortText_ReturnsSingleChunkWithPage` — one page (page 3, "Hello world.") → 1 chunk, index 0, page 3, text "Hello world.".
  - `Chunk_LongText_RespectsSizeAndOverlap` — size 10 tokens (40 chars), overlap 2 (8 chars), 200 chars of words → every chunk ≤ 40 chars, >1 chunk, consecutive chunks share text (end of chunk n appears at start of chunk n+1).
  - `Chunk_NeverSpansPages` — two pages → each chunk has exactly one page number, indexes 0..n-1 sequential across pages.
  - `Chunk_WhitespaceOnlyPage_Skipped` — page "   " → no chunks.
  - `Ctor_OverlapNotLessThanSize_Throws` — `new TextChunker(100, 100)` throws `ArgumentException`.
  - `Extract_Txt_ReturnsSinglePageWithNullPageNumber`, `Extract_EmptyTxt_ReturnsNoPages`, `Extract_Docx_ReturnsParagraphText` (build a docx in-memory with OpenXml), `Extract_Pdf_ReturnsTextPerPage` (build a 2-page PDF with PdfPig `PdfDocumentBuilder`, assert pages 1 and 2 text), `Extract_UnsupportedExtension_Throws`.
- [ ] Step 2: `dotnet test` → FAIL (types missing).
- [ ] Step 3: Implement. Chunker: normalize whitespace, window of `size*4` chars, back off to last `\n\n`, `. `, or space within the last 20% of the window; next start = end − `overlap*4` (always advancing). Extractor: PdfPig `page.Text` via `ContentOrderTextExtractor`; DOCX body paragraphs joined with `\n`; TXT via `StreamReader` (UTF-8, BOM detection).
- [ ] Step 4: `dotnet test` → all PASS.
- [ ] Step 5: Commit `feat: document text extraction and chunking`.

### Task 3: Ollama client and Qdrant store

**Files:**
- Create: `backend/MiniRag.Api/Infrastructure/OllamaClient.cs`, `backend/MiniRag.Api/Infrastructure/QdrantVectorStore.cs`, `backend/MiniRag.Api/Models/VectorModels.cs`

**Interfaces:**
- Produces:
  - `OllamaClient.ListModelsAsync(ct) : Task<IReadOnlyList<string>>` (`GET /api/tags`); `EmbedAsync(IReadOnlyList<string> inputs, ct) : Task<float[][]>` (`POST /api/embed`, model = EmbeddingModel); `ChatAsync(string system, string user, ct) : Task<string>` (`POST /api/chat`, `stream:false`, `think: EnableThinking`, `options.temperature: 0.1`); static `HasModel(IEnumerable<string> available, string model) : bool` (treats `name` and `name:latest` as equal).
  - `record ChunkPayload(string DocumentId, string DocumentName, int? PageNumber, int ChunkIndex, string Text)`; `record SearchHit(ChunkPayload Payload, double Score)`.
  - `QdrantVectorStore.EnsureCollectionAsync(int dimension, ct)` (create cosine collection + payload index on `documentId`; if exists with other size throw `InvalidOperationException` naming both sizes); `UpsertAsync(IReadOnlyList<(ChunkPayload, float[])> points, ct)` (point id = new Guid, `wait=true`); `SearchAsync(float[] vector, int topK, ct) : Task<IReadOnlyList<SearchHit>>`; `DeleteDocumentAsync(string documentId, ct)`; `ListDocumentsAsync(ct) : Task<IReadOnlyList<(string Id, string Name, int Chunks)>>` (scroll, group by documentId).
- Both registered as typed HttpClients (base address from options, Ollama timeout from `TimeoutSeconds`).

- [ ] Step 1: Unit test `HasModel_MatchesLatestTag` (`["nomic-embed-text:latest","qwen3:8b"]` has `nomic-embed-text` and `qwen3:8b`, not `llama3`).
- [ ] Step 2: Implement both classes.
- [ ] Step 3: `dotnet test` PASS; commit `feat: Ollama and Qdrant REST clients`.

### Task 4: Indexing, registry, RAG, startup checks, controllers

**Files:**
- Create: `backend/MiniRag.Api/Services/DocumentRegistry.cs`, `Services/DocumentIndexingService.cs`, `Services/RagService.cs`, `Services/StartupInitializer.cs` (IHostedService), `Controllers/DocumentsController.cs`, `Controllers/ChatController.cs`, `Controllers/HealthController.cs`, `Models/Dtos.cs`
- Modify: `Program.cs` (DI, request size limit `MaxUploadMb`)

**Interfaces:**
- Consumes: Tasks 2–3.
- Produces (HTTP, consumed by Task 6):
  - `GET /api/documents` → `DocumentDto[] { id, fileName, status: "Pending"|"Indexing"|"Indexed"|"Failed", chunkCount, error?, uploadedAt }`
  - `POST /api/documents` (multipart field `file`) → 202 `DocumentDto`; 400 `{ error }` for unsupported/empty.
  - `DELETE /api/documents/{id}` → 204.
  - `POST /api/chat` `{ question }` → `{ answer, insufficientContext: bool, citations: CitationDto[] { sourceNumber, documentName, pageNumber?, chunkIndex, score, snippet } }`; 400 on empty question.
  - `GET /api/health` → `{ ollama: { reachable, chatModel, chatModelAvailable, embeddingModel, embeddingModelAvailable, embeddingDimension }, qdrant: { reachable, collection } }`.
- `RagService`: hits filtered by `MinScore`; none → sentinel answer, `insufficientContext=true`, no chat call. Otherwise system prompt instructs: answer only from numbered sources, cite as `[1]`, reply with the exact sentinel when sources lack the answer; strip any `<think>…</think>`. If the answer contains the sentinel → `insufficientContext=true`, citations empty. Else citations = sources actually referenced `[n]` in the answer (fallback: all retrieved).
- Indexing: save to `UploadPath/{id}{ext}`, status Pending → background `Task.Run` with scoped services: Indexing → extract → chunk (0 chunks → Failed "No extractable text") → embed in batches of 16 with `DocumentPrefix` → upsert → Indexed (chunkCount) | Failed(error).
- Startup: check models (log warning with `ollama pull` hint if missing, no pull), probe-embed to detect dimension (store in a singleton `EmbeddingInfo { int Dimension }`), `EnsureCollectionAsync`, rebuild registry from `ListDocumentsAsync` as Indexed.

- [ ] Step 1: Implement registry + services + controllers.
- [ ] Step 2: `dotnet build` clean; `dotnet run`; `GET /api/health` shows both models available and `embeddingDimension` (expect 768, verify not assume).
- [ ] Step 3: Commit `feat: indexing pipeline, RAG answering and API endpoints`.

### Task 5: Backend end-to-end verification

**Files:**
- Create: `samples/contoso-handbook.pdf` (generated, 3 pages, distinctive facts on distinct pages), `samples/make-sample.csx` or test helper that generated it; `scripts/e2e.ps1`

- [ ] Step 1: Sample facts: p1 company founded 2011 in Lisbon by Marta Okafor; p2 remote work: 3 days per week, equipment stipend 450 EUR; p3 vacation 27 days, carry-over max 5 days until 31 March.
- [ ] Step 2: `scripts/e2e.ps1`: upload sample → poll until Indexed → ask "How many vacation days do employees get?" (expect "27", citation page 3) → ask "What is the equipment stipend?" (expect 450, page 2) → ask "What is the CEO's favourite football team?" (expect `insufficientContext=true`) → upload `.xlsx`-named file (expect 400).
- [ ] Step 3: Run; record answers and timings. Tune `MinScore` if unanswerable question isn't rejected or answerable ones are.
- [ ] Step 4: Commit `test: sample document and e2e script`.

### Task 6: Angular frontend

**Files:**
- Create: `frontend/` via `npx @angular/cli new frontend --standalone --style=css --routing=false --ssr=false --skip-git`; `frontend/proxy.conf.json` (`/api` → `http://localhost:5080`); `src/app/api.service.ts`; `src/app/app.ts|html|css`.

**Interfaces:** consumes Task 4 HTTP contracts.

- [ ] Step 1: Implement: upload (file input accept `.pdf,.docx,.txt` + button, shows error), documents table with status + delete, poll every 2 s while any Pending/Indexing, chat input + history list, answer text, citations list (`[n] name · p. X · chunk Y · score`) with `<details>` snippet, "thinking…" indicator.
- [ ] Step 2: `npm run build` succeeds; `npm start` and `ng serve` proxy works (`GET http://localhost:4200/api/documents` returns JSON).
- [ ] Step 3: Commit `feat: Angular UI`.

### Task 7: README and final verification

**Files:** Create `README.md`.

- [ ] Step 1: README: prerequisites, install Ollama, `ollama pull qwen3:8b` / `nomic-embed-text`, `docker compose up -d`, `dotnet run --project backend/MiniRag.Api`, `cd frontend && npm install && npm start`, test steps, configuration table, evaluation notes (timings observed in Task 5).
- [ ] Step 2: Fresh run of `dotnet test` and `scripts/e2e.ps1`, both pass.
- [ ] Step 3: Commit `docs: README with setup and testing instructions`.
