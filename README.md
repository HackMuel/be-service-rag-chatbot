# Internal RAG Chatbot Backend — .NET, Qdrant, MinIO, Ollama

## 1. Deskripsi

`be_service` adalah backend chatbot **RAG (Retrieval-Augmented Generation)** internal untuk
menjawab pertanyaan karyawan berdasarkan **dokumen internal** (PDF/TXT) — bukan pengetahuan
bebas model. Tujuannya menekan halusinasi dengan memaksa jawaban **grounded** ke isi dokumen.

Arsitektur bersifat **Qdrant-centric**: file asli disimpan di Object Storage (MinIO),
chunk content + metadata retrieval disimpan sebagai payload di Qdrant, dan PostgreSQL/Supabase
berperan sebagai *document registry* (metadata administratif). Tabel `document_chunks` di
Postgres bersifat opsional/legacy.

Query path memilih salah satu dari **3 level retrieval**: (1) exact/structured via filter
payload, (2) deterministic template, (3) grounded semantic/policy via hybrid search + LLM.
Lihat [docs/RETRIEVAL.md](docs/RETRIEVAL.md).

## 2. Arsitektur — Clean Architecture (3 layer)

Project di-migrasi dari 1 project flat menjadi **3 project** dengan boundary yang
**ditegakkan compiler** (alur runtime tidak berubah, hanya penempatan file):

```text
            ┌──────────────────────────────┐
            │       be_service (Api)        │  Program.cs, middleware, endpoints
            │      ref: Core + Infra        │
            └───────────────┬──────────────┘
                            │ depends on
              ┌─────────────┴─────────────┐
              v                           v
   ┌────────────────────┐   ┌──────────────────────────────┐
   │   be_service.Core  │◄──│   be_service.Infrastructure   │
   │  Models            │ref│  QdrantService, OllamaService │
   │  Abstractions (8)  │   │  ObjectStorageService,        │
   │  Services (logika) │   │  Repositories, PdfTextExtractor│
   │  Exceptions        │   │  (implementasi port → Core)   │
   │  ── NO infra dep ──│   └──────────────────────────────┘
   └────────────────────┘
```

**Dependency rule:** `Core` tidak punya dependency infra apa pun. Package infra
(`Qdrant.Client`, `Npgsql`, `Minio`, `Pgvector`, `PdfPig`, `iText7`) **hanya** dideklarasikan di
`be_service.Infrastructure.csproj`. Akibatnya `Core` **tidak bisa** mengimpor Qdrant/Npgsql/Minio
— pelanggaran boundary = gagal compile, bukan sekadar konvensi.

**8 ports** (`be_service.Core/Abstractions/`) — kontrak yang diimplementasi Infrastructure:

| Port | Peran |
|---|---|
| `IChatService` | Orchestrator chat (analisis → retrieval → jawaban) |
| `IEmbeddingService` | Embedding dense via Ollama |
| `IVectorStore` | Vector + payload store (Qdrant) |
| `IBlobStore` | File asli PDF/TXT (MinIO) |
| `IEntityCatalog` | Katalog known-entity dari korpus (grounding) |
| `IDocumentRepository` | Registry `documents` (PostgreSQL) |
| `IChunkStore` | Penyimpanan chunk opsional/legacy (`document_chunks`) |
| `IDocumentTextExtractor` | Ekstraksi teks dari PDF/TXT |

## 3. Tech Stack

| Komponen | Teknologi |
|---|---|
| API | ASP.NET Core Minimal API (.NET 10), C# |
| Vector store | Qdrant 1.18.1 — named vectors `dense` (768, Cosine) + `sparse` (BM25), fusi RRF |
| LLM & embedding | Ollama — chat `qwen2.5:1.5b`, embedding `nomic-embed-text` |
| Object storage | MinIO (file asli PDF/TXT) |
| Document registry | PostgreSQL/Supabase (`documents`; `document_chunks` opsional) |
| PDF extraction | UglyToad.PdfPig (koordinat) + iText7 |
| Frontend | React (repository terpisah) |

## 4. Struktur Project

```text
be_service.slnx                       # solution root (build dari sini)
├── be_service.csproj                 # Api: Program.cs, Services/ (middleware), endpoints
├── be_service.Core/                  # domain & logika murni (tanpa dep infra)
│   ├── Abstractions/                 # 8 ports
│   ├── Models/
│   ├── Services/                     # RagChatService, QueryUnderstanding, Retrieval, Ingestion, dst.
│   └── Exceptions/
├── be_service.Infrastructure/        # adapter infra (implementasi port)
│   ├── Services/                     # QdrantService, OllamaService, ObjectStorageService, PdfTextExtractor
│   └── Repositories/                 # DocumentRepository, ChunkRepository
└── Tests/BeService.Tests/            # kerangka test (di-exclude dari build Api)
```

## 5. Setup & Run

**Prasyarat:** .NET 10 SDK; Qdrant, MinIO, Ollama, PostgreSQL/Supabase berjalan sebelum upload.

```bash
# Qdrant
docker run -p 6333:6333 qdrant/qdrant

# MinIO (console http://localhost:9001 — minioadmin/minioadmin; bucket rag-documents auto-dibuat)
docker run -d -p 9000:9000 -p 9001:9001 --name minio \
  -e "MINIO_ROOT_USER=minioadmin" -e "MINIO_ROOT_PASSWORD=minioadmin" \
  quay.io/minio/minio server /data --console-address ":9001"

# Ollama (http://localhost:11434)
ollama serve
ollama pull nomic-embed-text
ollama pull qwen2.5:1.5b
```

**PostgreSQL:** pastikan tabel `documents` tersedia; tambahkan kolom object storage via
`Sql/add_object_storage_columns.sql`. (`document_chunks` hanya perlu bila
`StorageMode.WriteDocumentChunksToPostgres=true`.)

**Konfigurasi:** salin `appsettings.Example.json` → `appsettings.json` (gitignored) lalu isi rahasia,
**atau** sediakan rahasia via environment variable (double underscore), mis. `Security__ApiKey`,
`ConnectionStrings__SupabaseDb`, `ObjectStorage__AccessKey`, `ObjectStorage__SecretKey`.
Nilai non-rahasia (Qdrant/Ollama/Retrieval/StorageMode/Rag) sudah ber-default di template.
`Rag.Mode` = `Legacy` | `Hybrid` | `Semantic` (aktif: **Semantic**).

**Build & run dari root solution:**

```bash
dotnet restore be_service.slnx
dotnet build be_service.slnx
dotnet run --project be_service.csproj      # default http://localhost:5057
```

Init collection lalu upload dokumen:

```bash
curl http://localhost:5057/api/qdrant/init -H "X-API-Key: <key>"
curl -X POST http://localhost:5057/api/upload-pdf -H "X-API-Key: <key>" -F "file=@dokumen.pdf"
```

## 6. API Endpoints

> Semua endpoint memerlukan header `X-API-Key` (nilai `Security:ApiKey` / env `Security__ApiKey`).
> Endpoint health-check belum terpasang.

| Method | Path | Fungsi |
|---|---|---|
| POST | `/api/chat` | Tanya jawab (`{ "message": "..." }` → `{ answer, sources }`) |
| POST | `/api/upload-pdf` | Upload PDF (form-data `file`) |
| POST | `/api/upload-txt` | Upload TXT (form-data `file`) |
| POST | `/api/ingest` | Ingest teks via JSON (`{ title, department, content }`) — debug |
| GET | `/api/qdrant/init` | Buat/pastikan collection Qdrant |
| POST | `/api/qdrant/recreate` | Drop + buat ulang collection (named vectors); semua data hilang |

Contoh chat:

```bash
curl -X POST http://localhost:5057/api/chat \
  -H "X-API-Key: <key>" -H "Content-Type: application/json" \
  -d '{"message":"Apa saja SOP Keamanan Area Kilang?"}'
```

## 7. Testing

Verifikasi fungsional saat ini **manual** (belum ada suite test otomatis; `Tests/` masih kerangka).

- **Gate baseline 17 query** — `scripts/baseline.sh` merekam/replay 17 query gate ke folder golden,
  dipakai untuk mendeteksi regresi setelah perubahan:

  ```bash
  export API_KEY=<isi Security:ApiKey>
  ./scripts/baseline.sh                      # tulis ke tests/baseline/golden/
  git diff tests/baseline/golden             # pastikan tak ada regresi
  ```

  Prasyarat: server jalan + data sudah di-ingest bersih; butuh `jq`.

- **Test cases manual** lengkap (Level 1/2/3 + negative/edge) → [docs/MANUAL_TEST_CASES.md](docs/MANUAL_TEST_CASES.md),
  panduan di [docs/MANUAL_TESTING.md](docs/MANUAL_TESTING.md).

## 8. Dokumentasi Lanjutan

| Dokumen | Isi |
|---|---|
| [docs/PRD.md](docs/PRD.md) | Product requirements, status fitur, roadmap |
| [docs/CODEBASE_GUIDE.md](docs/CODEBASE_GUIDE.md) | Arsitektur teknis & peta kode (onboarding) |
| [docs/RETRIEVAL.md](docs/RETRIEVAL.md) | Three-level retrieval deep-dive |
| [docs/LOGGING.md](docs/LOGGING.md) | Log trace (`RETRIEVAL_TRACE`, `ANSWER_TRACE`, dll.) |
| [docs/MANUAL_TEST_CASES.md](docs/MANUAL_TEST_CASES.md), [docs/MANUAL_TESTING.md](docs/MANUAL_TESTING.md) | Test cases & panduan uji manual |
| [docs/TECH_DEBT.md](docs/TECH_DEBT.md) | Catatan utang teknis |
