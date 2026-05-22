# Internal RAG Chatbot Backend - .NET, Supabase, Qdrant, Ollama

## 1. Deskripsi Singkat

Project ini adalah backend chatbot RAG internal perusahaan untuk mencari dan menjawab informasi dari dokumen internal. Backend menggunakan pendekatan hybrid retrieval: exact/filter lookup untuk data terstruktur, deterministic template untuk pertanyaan yang pasti, dan grounded LLM untuk pertanyaan fleksibel yang membutuhkan pemahaman konteks.

Sistem dirancang on-premise/local-first:

- Supabase/PostgreSQL menjadi source of truth untuk dokumen, chunk content, dan metadata.
- Qdrant menjadi vector index untuk semantic search, dengan point id yang sama seperti `document_chunks.id`.
- Ollama digunakan secara lokal untuk embedding dan LLM generation.
- Frontend React berkomunikasi melalui API backend .NET Minimal API.

## 2. Tujuan Project

Tujuan utama project ini:

- Membantu pencarian informasi internal perusahaan dari dokumen PDF/TXT.
- Menjawab pertanyaan berdasarkan context dokumen, bukan dari pengetahuan bebas model.
- Mengurangi hallucination dengan memisahkan exact retrieval, deterministic answer, dan semantic RAG.
- Menyediakan arsitektur RAG yang lebih production-like: PostgreSQL sebagai source of truth dan Qdrant sebagai vector-only index.
- Mendukung retrieval data karyawan, SOP, audit, maintenance, rekap lembur, dan profil perusahaan.

## 3. Tech Stack

| Komponen | Teknologi |
|---|---|
| Backend API | .NET / ASP.NET Core Minimal API |
| Bahasa | C# |
| Database utama | Supabase/PostgreSQL |
| Vector database | Qdrant |
| Embedding lokal | Ollama `nomic-embed-text` |
| LLM lokal | Ollama `qwen2.5:1.5b` |
| PDF extraction | UglyToad.PdfPig |
| PostgreSQL driver | Npgsql |
| Frontend | React, repository terpisah |
| File ingestion | PDF/TXT |
| Local services | Qdrant dan Ollama, via Docker

## 4. High-Level Architecture
```text
Frontend React
    |
    v
Backend .NET Minimal API
    |
    v
RagChatService
    |
    v
QueryAnalyzerService
    |
    v
RetrievalService
    |
    +--> Supabase/PostgreSQL
    |       - documents
    |       - document_chunks
    |
    +--> Qdrant
    |       - vector index
    |       - point id = chunk id
    |
    +--> Ollama
            - embedding
            - LLM generation
    |
    v
AnswerFormatterService / PromptBuilderService
    |
    v
ChatResponse
```

Peran utama:

- Supabase/PostgreSQL: source of truth untuk dokumen, chunk content, dan metadata.
- Qdrant: semantic vector index. Qdrant tidak lagi menjadi tempat utama content/metadata.
- Ollama: membuat embedding dan menghasilkan jawaban LLM jika deterministic answer tidak cukup.
- Object Storage: rencana production untuk menyimpan file asli PDF/TXT, misalnya MinIO atau Supabase Storage.

## 5. Data Architecture

### Tabel `documents`

Menyimpan metadata dokumen.

Kolom utama:

- `id`
- `title`
- `source_type`
- `file_name`
- `department`
- `created_at`

### Tabel `document_chunks`

Menyimpan chunk content dan metadata. Tabel ini adalah source of truth untuk retrieval context.

Kolom utama:

- `id uuid`
- `document_id uuid`
- `chunk_index int`
- `content text`
- `metadata jsonb`
- `created_at timestamptz`

Prinsip penting:

```text
document_chunks.id = Qdrant point id
```

Metadata JSONB menyimpan field seperti:

- `recordType`
- `nik`
- `name`
- `nameNormalized`
- `maintenanceCode`
- `date`
- `division`
- `department`
- `position`
- `shift`
- `employeeStatus`
- `duration`
- `approval`
- `equipment`
- `location`
- `maintenanceStatus`
- `technician`
- `sectionTitle`
- `documentTitle`

### Qdrant

Qdrant menyimpan:

- point id / chunk id
- vector embedding

Qdrant tidak menjadi source of truth untuk content dan metadata. Setelah semantic search, backend mengambil `chunkId` dari Qdrant lalu membaca chunk lengkap dari Supabase/PostgreSQL.

## 6. Workflow Upload / Ingestion

```text
User upload PDF/TXT
    |
    v
Backend validate file
    |
    v
Extract text
    |
    v
Normalize text
    |
    v
Chunking
    |
    v
Save chunk + metadata to Supabase document_chunks
    |
    v
Generate embedding via Ollama
    |
    v
Upsert vector to Qdrant using the same chunkId
```

Service yang terlibat:

| Service | Tanggung jawab |
|---|---|
| `IngestionService` | Entry point ingestion dari endpoint upload/API |
| `PdfTextExtractor` | Extract text dari file PDF menggunakan PdfPig |
| `TextNormalizer` | Membersihkan dan menormalisasi text hasil extraction |
| `ChunkingService` | Memecah text menjadi chunks sesuai section/structured row |
| `DocumentIngestionOrchestrator` | Mengatur flow ingestion end-to-end |
| `EmbeddingIngestionService` | Memanggil embedding model melalui Ollama |
| `ChunkRepository` | Menyimpan chunk content dan metadata ke Supabase/PostgreSQL |
| `QdrantPointWriter` | Menyimpan vector embedding ke Qdrant |
| `OllamaService` | Client untuk embedding dan LLM lokal |

Structured data seperti karyawan, lembur, dan maintenance diproses menjadi 1 row = 1 chunk agar exact/filter retrieval lebih akurat.

## 7. Workflow Chat / Question Answering

```text
Frontend POST /api/chat
    |
    v
RagChatService
    |
    v
QueryAnalyzerService
    |
    v
RetrievalService
    |
    v
AnswerFormatterService or PromptBuilderService
    |
    v
Ollama if needed
    |
    v
ChatResponse
```

Penjelasan:

1. Frontend mengirim pertanyaan ke `/api/chat`.
2. `RagChatService` menjadi orchestrator utama.
3. `QueryAnalyzerService` menganalisis intent pertanyaan.
4. `RetrievalService` memilih strategi retrieval.
5. `AnswerFormatterService` mencoba menjawab secara deterministic.
6. Jika deterministic answer tidak tersedia, `PromptBuilderService` membuat prompt grounded.
7. `OllamaService` memanggil LLM lokal.
8. Backend mengembalikan `ChatResponse`.

## 8. Three-Level Retrieval Strategy

### Level 1 - ExactStructured

Dipakai untuk data terstruktur:

- NIK
- MT-code
- tanggal
- divisi
- status karyawan
- approval lembur
- shift
- jabatan
- lokasi maintenance
- teknisi maintenance
- maintenance status

Flow:

```text
Supabase/PostgreSQL
    |
    v
ChunkRepository
    |
    v
AnswerFormatterService
    |
    v
Response
```

Karakteristik:

- Tidak memakai Qdrant.
- Tidak memakai LLM.
- Cepat dan deterministic.
- Cocok untuk data exact dan filter metadata.

Contoh query:

- `Siapa karyawan dengan NIK RU6-1030?`
- `Tampilkan seluruh karyawan kontrak`
- `Tampilkan data karyawan divisi Keuangan`
- `Berikan data maintenance kode MT-308`
- `Tampilkan lembur approval Pending`

### Level 2 - DeterministicTemplate

Dipakai untuk pertanyaan yang sering, pasti, dan jawabannya bisa diambil langsung dari context.

Contoh:

- daftar SOP
- kecepatan kendaraan
- backup otomatis server
- kepatuhan APD
- kapasitas produksi
- nama unit perusahaan
- direktur operasi

Flow:

```text
Supabase recordType lookup
    |
    v
AnswerFormatterService template
    |
    v
Response
```

Karakteristik:

- Tidak memakai Qdrant jika recordType context cukup.
- Tidak memakai LLM jika template berhasil.
- Mengurangi latency dan hallucination.

Contoh query:

- `Apa saja SOP Keamanan Area Kilang?`
- `Berapa kecepatan maksimal kendaraan di area produksi?`
- `Jam berapa backup otomatis server internal dilakukan?`
- `Apa tingkat kepatuhan APD?`
- `Apa nama unit perusahaan?`
- `Berapa kapasitas produksi Pertamina RU VI Balongan?`

### Level 3 - PolicyGrounded / SemanticGrounded

Dipakai untuk pertanyaan fleksibel, policy/izin, reasoning ringan, ringkasan, atau pertanyaan dengan bahasa yang tidak persis sama dengan dokumen.

Contoh:

- `Apakah orang IT boleh masuk area penyimpanan?`
- `Kalau pekerja selain HSSE ingin masuk area tangki, apakah diperbolehkan?`
- `Apa risiko membawa perangkat elektronik biasa ke area kilang?`
- `Bagaimana prosedur keamanan sebelum pekerja masuk area kilang?`
- `Ringkas isi dokumen ini dari sisi keamanan dan operasional.`

Flow jika context recordType cukup:

```text
Supabase recordType lookup
    |
    v
PromptBuilderService policy/general prompt
    |
    v
Ollama LLM
    |
    v
Response
```

Flow jika perlu semantic search:

```text
Ollama embedding
    |
    v
Qdrant vector search
    |
    v
chunkId list
    |
    v
Supabase GetChunksByIds
    |
    v
semantic context filtering
    |
    v
PromptBuilderService
    |
    v
Ollama LLM
    |
    v
Response
```

Untuk pertanyaan policy, prompt khusus melarang LLM membuat izin/aturan baru dan menegaskan bahwa jika context menyebut kata `hanya`, pihak yang tidak disebut tidak boleh diasumsikan punya izin.

## 9. Service Responsibilities

| Service / Class | Tanggung jawab |
|---|---|
| `RagChatService` | Orchestrator chat: analisis, retrieval, formatting, prompt, LLM, response |
| `QueryAnalyzerService` | Mendeteksi intent, answer level, exact key, filter, record type, dan policy question |
| `RetrievalService` | Memilih retrieval strategy: exact, recordType, semantic Qdrant, context filtering |
| `AnswerFormatterService` | Membuat deterministic answer untuk structured/profile/audit/SOP template |
| `PromptBuilderService` | Membuat prompt grounded umum dan policy-grounded |
| `OllamaService` | Client untuk embedding dan LLM generation lokal |
| `ChunkRepository` | Akses PostgreSQL untuk `document_chunks`, exact/filter lookup, dan chunk fetch by id |
| `QdrantService` | Facade agar caller lama tetap stabil |
| `QdrantSearchClient` | Vector search ke Qdrant |
| `QdrantPointWriter` | Upsert vector-only point ke Qdrant |
| `ChunkMetadataExtractor` | Extract metadata dari chunk content |
| `RetrievedChunkMapper` | Mapping result Qdrant minimal id/score ke model `RetrievedChunk` |
| `DocumentIngestionOrchestrator` | Orkestrasi ingestion dokumen |
| `TextNormalizer` | Normalisasi text PDF/TXT |
| `ChunkingService` | Split text menjadi chunks |
| `PdfTextExtractor` | Extract text dari PDF |
| `EmbeddingIngestionService` | Generate embedding untuk chunk saat ingestion |

## 10. API Endpoints

### `POST /api/chat`

Mengirim pertanyaan user ke chatbot.

Request:

```json
{
  "message": "Siapa karyawan dengan NIK RU6-1030?"
}
```

Response:

```json
{
  "answer": "Data Karyawan:\n- NIK: RU6-1030\n  Nama: Budi Santoso\n  Divisi: Human Capital\n  Jabatan: Staff\n  Shift: B\n  Status: Tetap",
  "sources": ["Dummy_Data_Internal_Pertamina_RUVI_Balongan.pdf"]
}
```

### `POST /api/upload-pdf`

Upload file PDF.

Contoh:

```bash
curl -X POST http://localhost:5057/api/upload-pdf \
  -F "file=@Dummy_Data_Internal_Pertamina_RUVI_Balongan.pdf"
```

Response:

```json
{
  "message": "PDF uploaded successfully",
  "documentId": "00000000-0000-0000-0000-000000000000"
}
```

### `POST /api/upload-txt`

Upload file TXT.

Contoh:

```bash
curl -X POST http://localhost:5057/api/upload-txt \
  -F "file=@data-internal.txt"
```

### `POST /api/ingest`

Ingest text langsung melalui JSON.

Request:

```json
{
  "title": "Dokumen Internal",
  "department": "General",
  "content": "Isi dokumen..."
}
```

### `GET /api/qdrant/init`

Membuat atau memastikan collection Qdrant tersedia.

```bash
curl http://localhost:5057/api/qdrant/init
```

## 11. Environment Configuration

Contoh `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "SupabaseDb": "Host=localhost;Port=54322;Database=postgres;Username=postgres;Password=postgres"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

Konfigurasi yang dipakai saat ini:

| Konfigurasi | Nilai saat ini |
|---|---|
| PostgreSQL connection string | `ConnectionStrings:SupabaseDb` |
| Qdrant base URL | `http://localhost:6333` |
| Qdrant collection | `pertamina_chunks` |
| Embedding model | `nomic-embed-text` |
| Chat model | `qwen2.5:1.5b` |
| Vector size | `768` |

Catatan production:

- Jangan simpan secret asli di repository.
- Gunakan environment variables atau secret manager.
- Qdrant URL, Ollama URL, collection name, dan model name sebaiknya dipindahkan ke configuration.

## 12. Installation & Running Locally

### 1. Clone repository

```bash
git clone <repository-url>
cd be_service
```

### 2. Install .NET SDK

Project ini menggunakan target framework:

```text
net10.0
```

Pastikan SDK yang sesuai sudah terpasang.

### 3. Jalankan Qdrant

```bash
docker run -p 6333:6333 qdrant/qdrant
```

### 4. Jalankan Ollama

```bash
ollama serve
```

Pull model yang dipakai:

```bash
ollama pull nomic-embed-text
ollama pull qwen2.5:1.5b
```

### 5. Setup Supabase/PostgreSQL

Pastikan PostgreSQL/Supabase berjalan dan extension vector tersedia.

Jalankan schema SQL minimal dari:

```text
supabase/snippets/Untitled query 731.sql
```

### 6. Configure `appsettings.json`

Sesuaikan connection string:

```json
"ConnectionStrings": {
  "SupabaseDb": "Host=...;Port=...;Database=...;Username=...;Password=..."
}
```

### 7. Restore, build, run

```bash
dotnet restore
dotnet build
dotnet run
```

### 8. Init Qdrant collection

```bash
curl http://localhost:5057/api/qdrant/init
```

### 9. Upload dokumen

```bash
curl -X POST http://localhost:5057/api/upload-pdf \
  -F "file=@dokumen.pdf"
```

### 10. Test chat

```bash
curl -X POST http://localhost:5057/api/chat \
  -H "Content-Type: application/json" \
  -d "{\"message\":\"Apa saja SOP Keamanan Area Kilang?\"}"
```

## 13. Database Setup

Schema minimal:

```sql
create extension if not exists vector;

create table if not exists documents (
  id uuid primary key default gen_random_uuid(),
  title text not null,
  source_type text default 'text',
  file_name text,
  department text,
  created_at timestamptz default now()
);

create table if not exists document_chunks (
  id uuid primary key default gen_random_uuid(),
  document_id uuid references documents(id) on delete cascade,
  chunk_index int not null,
  content text not null,
  embedding vector(768),
  metadata jsonb default '{}'::jsonb,
  created_at timestamptz default now()
);
```

Index metadata yang direkomendasikan:

```sql
create index if not exists idx_document_chunks_metadata_record_type
on document_chunks ((metadata->>'recordType'));

create index if not exists idx_document_chunks_metadata_nik
on document_chunks ((metadata->>'nik'));

create index if not exists idx_document_chunks_metadata_name_normalized
on document_chunks ((metadata->>'nameNormalized'));

create index if not exists idx_document_chunks_metadata_maintenance_code
on document_chunks ((metadata->>'maintenanceCode'));

create index if not exists idx_document_chunks_metadata_date
on document_chunks ((metadata->>'date'));

create index if not exists idx_document_chunks_metadata_division
on document_chunks ((metadata->>'division'));

create index if not exists idx_document_chunks_metadata_shift
on document_chunks ((metadata->>'shift'));

create index if not exists idx_document_chunks_metadata_employee_status
on document_chunks ((metadata->>'employeeStatus'));

create index if not exists idx_document_chunks_metadata_approval
on document_chunks ((metadata->>'approval'));

create index if not exists idx_document_chunks_metadata_location
on document_chunks ((metadata->>'location'));

create index if not exists idx_document_chunks_metadata_maintenance_status
on document_chunks ((metadata->>'maintenanceStatus'));

create index if not exists idx_document_chunks_metadata_technician
on document_chunks ((metadata->>'technician'));
```

## 14. Qdrant Setup

Collection Qdrant dibuat melalui:

```text
GET /api/qdrant/init
```

Konfigurasi saat ini:

- Collection: `pertamina_chunks`
- Vector size: `768`
- Distance: `Cosine`
- Point id: sama dengan `document_chunks.id`

Qdrant upsert saat ini menyimpan vector-only:

```json
{
  "id": "chunkId",
  "vector": [0.01, 0.02, "..."]
}
```

Content dan metadata tidak disimpan sebagai source of truth di Qdrant. Setelah Qdrant mengembalikan point id, backend mengambil chunk lengkap dari Supabase/PostgreSQL.

## 15. Testing Checklist

### Level 1 - ExactStructured

- [ ] `Siapa karyawan dengan NIK RU6-1030?`
- [ ] `Tampilkan seluruh karyawan kontrak`
- [ ] `Tampilkan data karyawan divisi Keuangan`
- [ ] `Berikan data maintenance kode MT-308`
- [ ] `Tampilkan lembur approval Pending`

Expected:

- `retrievalSource=postgres_exact`
- `qdrantVectorSearch=False`
- `usedLlm=False`

### Level 2 - DeterministicTemplate

- [ ] `Apa saja SOP Keamanan Area Kilang?`
- [ ] `Berapa kecepatan maksimal kendaraan di area produksi?`
- [ ] `Jam berapa backup otomatis server internal dilakukan?`
- [ ] `Apa tingkat kepatuhan APD?`
- [ ] `Apa nama unit perusahaan?`
- [ ] `Berapa kapasitas produksi Pertamina RU VI Balongan?`

Expected:

- `retrievalSource=postgres_record_type`
- `qdrantVectorSearch=False`
- `usedDeterministicAnswer=True`

### Level 3 - PolicyGrounded / SemanticGrounded

- [ ] `Apakah orang IT boleh masuk area penyimpanan?`
- [ ] `Kalau pekerja selain HSSE ingin masuk area tangki, apakah diperbolehkan?`
- [ ] `Apa risiko membawa perangkat elektronik biasa ke area kilang?`
- [ ] `Bagaimana prosedur keamanan sebelum pekerja masuk area kilang?`
- [ ] `Ringkas isi dokumen ini dari sisi keamanan dan operasional.`

Expected:

- Menggunakan grounded prompt.
- Tidak membuat aturan/izin baru.
- Jika semantic fallback aktif, log menunjukkan `source=qdrant_vector_then_postgres`.

### Negative / Edge Case

- [ ] `Apa itu anjing?`
- [ ] `RU6-9999`
- [ ] `MT-999`
- [ ] `Tampilkan karyawan divisi yang tidak ada`

Expected:

```text
Maaf, saya tidak menemukan informasi tersebut.
```

Untuk not found, `sources` harus kosong.

## 16. Logging & Trace

Log penting:

### `RETRIEVAL_TRACE`

Contoh:

```text
RETRIEVAL_TRACE answerLevel=SemanticGrounded mode=semantic source=qdrant_vector_then_postgres qdrantVectorSearch=True chunks=3
```

Makna:

- `answerLevel`: level decision layer yang dipakai.
- `mode`: mode retrieval teknis.
- `source`: sumber retrieval.
- `qdrantVectorSearch`: apakah Qdrant semantic search dipakai.
- `chunks`: jumlah chunk final yang masuk context.

### `ANSWER_TRACE`

Contoh:

```text
ANSWER_TRACE answerLevel=PolicyGrounded retrievalMode=sop source=postgres_record_type qdrantVectorSearch=False usedDeterministicAnswer=False usedLlm=True
```

Makna:

- `usedDeterministicAnswer=True`: jawaban dibuat oleh formatter template.
- `usedLlm=True`: jawaban dibuat oleh Ollama LLM.

### `SEMANTIC_FILTER`

Contoh:

```text
SEMANTIC_FILTER before=5 after=2 allowedTypes=audit,sop
```

Makna:

- `before`: jumlah chunk hasil fetch dari Supabase setelah Qdrant search.
- `after`: jumlah chunk final setelah filtering/reranking.
- `allowedTypes`: recordType yang diprioritaskan.

Jika log menunjukkan:

```text
source=qdrant_vector_then_postgres
qdrantVectorSearch=True
```

artinya semantic search benar-benar memakai Qdrant lalu mengambil content dari Supabase.

## 17. Current Limitations

Limitasi saat ini:

- Parser dan normalizer masih disesuaikan dengan format dokumen tertentu.
- File asli PDF/TXT belum disimpan ke object storage.
- Belum ada authentication/authorization.
- Belum ada ACL per user, role, atau department.
- Belum ada document versioning.
- Belum ada delete/reindex strategy yang lengkap.
- Belum ada background job ingestion untuk dokumen besar.
- Belum ada full integration test untuk retrieval end-to-end.
- LLM lokal bisa lambat tergantung model dan hardware.
- Jawaban semantic tetap bergantung pada kualitas embedding, ranking, context filtering, dan model LLM.
- Konfigurasi Qdrant/Ollama/model name masih hardcoded di beberapa service dan sebaiknya dipindahkan ke configuration.

## 18. Production Roadmap

Roadmap menuju production-ready:

- Tambahkan object storage untuk file asli, misalnya MinIO atau Supabase Storage.
- Tambahkan authentication untuk API backend.
- Tambahkan authorization dan ACL per user/department/document.
- Tambahkan document versioning.
- Tambahkan delete/reindex pipeline.
- Tambahkan background job queue untuk ingestion dokumen besar.
- Tambahkan retry, timeout, dan circuit breaker untuk Postgres, Qdrant, dan Ollama.
- Tambahkan monitoring latency, error rate, dan retrieval trace.
- Tambahkan integration tests untuk ingestion dan retrieval.
- Tambahkan rate limiting untuk endpoint chat/upload.
- Pindahkan secrets dan service URL ke environment variables.
- Tambahkan Docker Compose untuk backend, Qdrant, Postgres/Supabase, dan MinIO jika digunakan.
- Tambahkan streaming response jika frontend membutuhkan pengalaman chat yang lebih responsif.
- Tambahkan frontend auth dan session management.

## 19. Project Status

Status project:

```text
Prototype stable / production-like prototype
```

Project ini belum production final, tetapi arsitekturnya sudah mengarah ke production-like RAG:

- PostgreSQL/Supabase sebagai source of truth.
- Qdrant sebagai vector-only index.
- Exact/filter retrieval dipisahkan dari semantic retrieval.
- Deterministic template digunakan untuk pertanyaan pasti.
- Grounded LLM digunakan hanya saat dibutuhkan.
- Logging trace sudah membantu membuktikan jalur retrieval dan answer generation.

Sebelum dipakai sebagai sistem production penuh, project masih perlu security, ACL, object storage, reindex strategy, monitoring, test suite, dan deployment hardening.
