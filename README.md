# Mini RAG with Ollama

A small, local Retrieval-Augmented Generation app to evaluate Ollama before using it in a bigger project.
Upload PDF / DOCX / TXT files, ask questions, get answers from **local models only**, with source citations.

```
Angular (4200) ──proxy /api──▶ ASP.NET Core API (5080) ──▶ Ollama (11434)  embeddings + chat
                                        └──────────────────▶ Qdrant (6333)  vector search
```

## Prerequisites

- Windows/macOS/Linux, a GPU helps (developed on an 8 GB RTX 5050)
- [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js 20+](https://nodejs.org), [Docker Desktop](https://www.docker.com/products/docker-desktop/)

## Setup

### 1. Install Ollama
Download from <https://ollama.com/download> and install. Check: `ollama --version`.
(Optional: run it in Docker instead with `docker compose --profile ollama up -d` — needs the NVIDIA container toolkit for GPU.)

### 2. Pull the models (nothing is downloaded automatically)
```bash
ollama pull qwen3:8b          # chat model, ~5.2 GB
ollama pull nomic-embed-text  # embedding model, ~274 MB
```
The API checks at startup that both exist and tells you the exact `ollama pull` command if one is missing.

### 3. Start Qdrant
```bash
docker compose up -d
```
Dashboard: <http://localhost:6333/dashboard>. Data is kept in `./qdrant_storage`.

### 4. Run the API
```bash
dotnet run --project backend/MiniRag.Api
```
Listens on <http://localhost:5080>. Check `GET /api/health`: it reports model availability and the **detected**
embedding dimension (768 for `nomic-embed-text`). The Qdrant collection is created with that dimension.

### 5. Run the Angular UI
```bash
cd frontend
npm install
npm start
```
Open <http://localhost:4200>.

### 6. Try it
1. Upload [samples/contoso-handbook.pdf](samples/contoso-handbook.pdf) and wait for status **Indexed**.
2. Ask: *How many vacation days do employees get per year?* → "27", cited to **page 3**.
3. Ask: *What is the home office equipment stipend?* → "450 EUR", cited to **page 2**.
4. Ask: *What is the CEO's favourite football team?* → "The uploaded documents do not contain sufficient information…", no citations.

The first answer after starting Ollama is slow (the model loads into memory); later ones take a few seconds.

## Automated checks

```bash
dotnet test                                   # unit tests: chunking, extraction, prompt/citation parsing, registry
powershell -File scripts/e2e.ps1              # end-to-end against the running API, Ollama and Qdrant
```
The e2e script uploads the sample, asks three answerable questions (checking the answer *and* the cited page), one
unanswerable question, and checks unsupported/empty files and deletion.

## Configuration (`backend/MiniRag.Api/appsettings.json`)

Override with environment variables, e.g. `Ollama__ChatModel=qwen3:4b`.

| Setting | Default | Notes |
|---|---|---|
| `Ollama:BaseUrl` | `http://localhost:11434` | |
| `Ollama:ChatModel` | `qwen3:8b` | any pulled chat model |
| `Ollama:EmbeddingModel` | `nomic-embed-text` | changing it requires a new collection (dimension check will tell you) |
| `Ollama:EnableThinking` | `false` | qwen3 "thinking" mode: slower, rarely better for RAG |
| `Ollama:DocumentPrefix` / `QueryPrefix` | `search_document: ` / `search_query: ` | required by nomic-embed-text; set empty for other models |
| `Qdrant:Url` / `Qdrant:Collection` | `http://localhost:6333` / `documents` | |
| `Rag:ChunkSizeTokens` / `ChunkOverlapTokens` | `700` / `100` | approximated as 4 characters per token |
| `Rag:TopK` | `5` | |
| `Rag:MinScore` | `0.45` | cosine score below which chunks are ignored; see notes |
| `Rag:MaxUploadMb` | `50` | |

## Evaluation notes (RTX 5050 8 GB, qwen3:8b Q4, thinking off)

- Indexing the 3-page sample: ~12 s including cold-loading the embedding model.
- Answers: ~0.6–3 s warm; ~23 s for the very first question (model load).
- Cosine scores observed with `nomic-embed-text`: answerable questions' best chunk 0.67–0.87; an off-topic question
  0.46–0.51. Scores are compressed, so `MinScore` is a weak gate here; **the chat model's refusal did the real work** for the
  unanswerable question. Raise `MinScore` (~0.55) to reject obvious off-topic questions before calling the LLM — tune on your own documents.
- Citations are the sources the model actually referenced as `[n]`; if it references none, all retrieved sources are shown.

## Limitations (MVP)

- DOCX has no reliable page numbers, so DOCX citations show document name and chunk index only.
- Scanned PDFs without a text layer have no extractable text and end as **Failed** (no OCR).
- Indexing status is in memory: after an API restart, indexed documents are reloaded from Qdrant, but in-flight or failed uploads are forgotten.
- Single-user, no authentication, retrieval is dense-vector only (no re-ranking, no hybrid search).

## Layout

```
backend/MiniRag.Api   Controllers, Services (extractor, chunker, indexing, RAG), Infrastructure (Ollama/Qdrant clients), Options
backend/MiniRag.Tests xUnit tests
frontend              Angular app
samples               sample PDF
scripts/e2e.ps1       end-to-end check
docs/superpowers      design spec and implementation plan
```
