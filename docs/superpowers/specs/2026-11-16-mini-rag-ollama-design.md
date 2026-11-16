# Mini RAG with Ollama — Design

Date: 2026-11-16 · Status: approved

## Intent

An experimental MVP to evaluate Ollama's local AI capabilities (embeddings + chat) before
integrating it into a larger project. Success: upload a document, ask questions, get answers
generated entirely by local Ollama models, with source citations.

## Goals

- Upload PDF, DOCX, TXT; extract text; chunk with configurable size/overlap; keep document
  name, page number (when available) and chunk index.
- Embed chunks with Ollama (`nomic-embed-text`), store vectors + metadata in Qdrant.
- Answer questions: embed question with the same model, retrieve Top K, generate answer with
  `qwen3:8b`, return citations. Clearly say when the documents lack sufficient information.
- Minimal Angular UI: upload, document list with indexing status, chat, answers, citations.
- Everything configurable: Ollama URL, chat model, embedding model, Qdrant URL, chunk size,
  chunk overlap, Top K.

## Non-goals / anti-goals

No Gemini, Redis, RabbitMQ, Hangfire, SignalR, S3, authentication, or enterprise
infrastructure. No elaborate styling. Do not download models automatically. An answer that
cites the wrong source, or invents an answer not in the documents, is a failure even if the
pipeline "works".

## Constraints

- .NET 10 (`net10.0`, LTS), C#, ASP.NET Core Web API with controllers.
- Angular (latest, standalone components). Qdrant via Docker Compose. Ollama runs natively on
  the host (RTX 5050 8 GB fits qwen3:8b Q4 + nomic-embed-text).
- Single developer machine, Windows.

## Approaches

1. **Plain `HttpClient` against Ollama and Qdrant REST APIs — chosen.** No SDKs, every request
   visible, easy to port into the larger project; ideal for an evaluation.
2. Microsoft.Extensions.AI + Qdrant.Client — less code but hides what is being evaluated.
3. Semantic Kernel — heavier, contradicts "avoid unnecessary abstractions".

## Design

### Backend (`backend/MiniRag.Api`)

- **Controllers**: `DocumentsController` (`POST /api/documents` upload, `GET /api/documents`
  list, `DELETE /api/documents/{id}`), `ChatController` (`POST /api/chat`),
  `HealthController` (`GET /api/health`: models present, embedding dimension, Qdrant reachable).
- **Services** (one responsibility each):
  - `DocumentTextExtractor` — PDF via PdfPig (per page), DOCX via DocumentFormat.OpenXml
    (no reliable page numbers → page = null), TXT as UTF-8. Returns a list of `(page?, text)`.
  - `TextChunker` — token-approximate (≈4 chars/token) sliding window, size 700 / overlap 100
    tokens, prefers breaking on paragraph/sentence/whitespace boundaries; chunks never span
    pages so citations stay accurate.
  - `OllamaClient` — `/api/tags` (model check), `/api/embed` (batch embeddings),
    `/api/chat` (non-streaming, `think` configurable, default false).
  - `QdrantVectorStore` — REST: ensure collection (cosine, detected dimension), upsert points,
    search, delete by document id, scroll to rebuild document list.
  - `DocumentIndexingService` — orchestrates extract → chunk → embed → upsert; runs in a
    background `Task` after upload; tracks status in `DocumentRegistry` (in-memory,
    Pending / Indexing / Indexed / Failed + error message + chunk count).
  - `RagService` — embed question → search Top K → if no hit above `MinScore`, return the
    "insufficient information" answer without calling the LLM → else build a prompt with
    numbered sources and strict instructions to answer only from them and to reply with an
    exact sentinel sentence when they don't contain the answer → return answer + citations
    (document name, page, chunk index, score, snippet).
- **Startup**: verify both models exist in `/api/tags` (log clear `ollama pull` hint if not),
  embed a probe string to detect the embedding dimension, ensure the Qdrant collection exists
  with that dimension (fail clearly on mismatch), rebuild the registry from Qdrant payloads so
  the document list survives restarts.
- **Configuration** (`appsettings.json`): `Ollama:BaseUrl`, `Ollama:ChatModel`,
  `Ollama:EmbeddingModel`, `Ollama:EnableThinking`, `Qdrant:Url`, `Qdrant:Collection`,
  `Rag:ChunkSizeTokens`, `Rag:ChunkOverlapTokens`, `Rag:TopK`, `Rag:MinScore`,
  `Rag:MaxUploadMb`. CORS allows the Angular dev server.
- **Embedding prefixes**: nomic-embed-text expects task prefixes — `search_document: ` for
  chunks, `search_query: ` for questions (configurable).

### Frontend (`frontend/`)

Single page: file picker + upload button; document table (name, status, chunks, delete) that
polls every 2 s while anything is Pending/Indexing; chat box; answers with numbered citations
(document · page · chunk · score, expandable snippet). Dev server proxies `/api` to the API.

### Infrastructure

`docker-compose.yml` with `qdrant/qdrant`, ports 6333/6334, volume `./qdrant_storage`.
README with setup: install Ollama, pull models, start Qdrant, run API, run Angular, test.

### Testing

- xUnit unit tests for `TextChunker` and `DocumentTextExtractor` (TXT/DOCX/PDF).
- End-to-end run against real Ollama + Qdrant: upload a sample document, ask an answerable
  question (check answer + citation page), ask an unanswerable question (check the
  insufficient-information response).

## Left to the builder

Exact prompt wording, the `MinScore` default (to be tuned by observation), sample document
content, UI layout details.
