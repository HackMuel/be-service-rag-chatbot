# Internal RAG Chatbot Backend - .NET, Qdrant, MinIO, Ollama

## 1. Deskripsi Singkat

Project ini adalah backend chatbot RAG internal perusahaan untuk mencari dan menjawab informasi dari dokumen PDF/TXT. Backend menggunakan arsitektur Qdrant-centric: file asli disimpan di Object Storage/MinIO, chunk content dan metadata retrieval disimpan di payload Qdrant, sedangkan PostgreSQL/Supabase dipakai sebagai document registry dan metadata administratif.

Sistem memakai hybrid RAG 3 level:

- Level 1 Exact/Structured Retrieval untuk data terstruktur.
- Level 2 Deterministic Template untuk pertanyaan pasti dan sering.
- Level 3 Grounded Semantic/Policy Answer untuk pertanyaan fleksibel yang membutuhkan context dan LLM.

Tujuan utama sistem adalah menjawab berdasarkan dokumen internal, bukan berdasarkan pengetahuan bebas model.

## 2. Tujuan Project

Tujuan project:

- Membantu pencarian informasi internal perusahaan dari dokumen PDF/TXT.
- Mendukung pertanyaan tentang data karyawan, rekap lembur, log maintenance, SOP, audit, dan profil perusahaan.
- Mengurangi hallucination dengan memisahkan exact retrieval, deterministic answer, dan grounded LLM.
- Membuat chat query path lebih sederhana: user query -> Qdrant -> formatter/LLM.
- Menjadikan PostgreSQL/Supabase sebagai document registry/admin metadata, bukan retrieval chunk utama.
- Menyimpan file asli di Object Storage agar ingestion dan audit dokumen lebih production-like.

## 3. Tech Stack

| Komponen | Teknologi |
|---|---|
| Backend API | ASP.NET Core / .NET Minimal API |
| Bahasa | C# |
| Document registry | PostgreSQL/Supabase local |
| Retrieval store | Qdrant vector database |
| File storage | MinIO Object Storage |
| Embedding lokal | Ollama |
| LLM lokal | Ollama |
| PostgreSQL driver | Npgsql |
| Object storage SDK | MinIO .NET SDK |
| PDF extraction | UglyToad.PdfPig / iText |
| Frontend | React, repository terpisah |
| File ingestion | PDF/TXT |

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
StructuredEntityResolver
    |  (known entities dari Qdrant payload cache jika dibutuhkan)
    v
RetrievalService
    |
    +--> Qdrant
    |       - vector embedding
    |       - chunk content
    |       - metadata payload
    |
    +--> Ollama
    |       - embedding
    |       - LLM generation jika diperlukan
    |
    v
AnswerFormatterService / PromptBuilderService
    |
    v
ChatResponse
```

Komponen pendukung:

```text
Upload PDF/TXT
    |
    v
MinIO/Object Storage
    |
    v
documents table di PostgreSQL/Supabase
    |
    v
Ingestion pipeline
    |
    v
Qdrant vector + payload
```

Peran utama:

- MinIO/Object Storage: menyimpan file asli PDF/TXT.
- PostgreSQL/Supabase: menyimpan registry dokumen dan metadata administratif.
- Qdrant: retrieval store utama untuk chunk content, metadata payload, dan vector embedding.
- Ollama: membuat embedding dan menjalankan LLM lokal.
- React frontend: client yang memanggil endpoint backend.

## 5. Data Architecture

### Object Storage / MinIO

Object Storage menyimpan file asli yang di-upload user.

Contoh object key:

```text
documents/{documentId}/{safeFileName}
```

Object Storage dipakai agar file asli tetap tersedia untuk audit, reprocessing, atau re-ingestion.

### Tabel `documents`

Tabel ini menyimpan registry dokumen dan metadata administratif.

Kolom utama:

- `id`
- `title`
- `source_type`
- `file_name`
- `department`
- `storage_bucket`
- `storage_object_key`
- `content_type`
- `created_at`

Kolom object storage ditambahkan melalui:

```text
Sql/add_object_storage_columns.sql
```

### Tabel `document_chunks`

`document_chunks` sekarang bersifat optional/legacy/debug.

Jika konfigurasi berikut bernilai `true`, chunk juga ditulis ke PostgreSQL:

```json
"StorageMode": {
  "WriteDocumentChunksToPostgres": true
}
```

Jika bernilai `false`, ingestion tetap menyimpan file ke MinIO, registry ke `documents`, dan chunk retrieval ke Qdrant, tetapi tidak mengisi `document_chunks`.

### Qdrant

Qdrant adalah retrieval store utama. Setiap point menyimpan:

- point id / chunk id
- vector embedding
- payload lengkap

Payload Qdrant berisi:

- `documentId`
- `documentTitle`
- `content`
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
- `chunkIndex`

Contoh point:

```json
{
  "id": "chunk-guid",
  "vector": [0.01, 0.02, 0.03],
  "payload": {
    "documentId": "document-guid",
    "documentTitle": "Dummy_Data_Internal_Pertamina_RUVI_Balongan.pdf",
    "content": "Data Karyawan ...",
    "recordType": "employee",
    "nik": "RU6-1030",
    "name": "Budi Santoso",
    "nameNormalized": "BUDI SANTOSO",
    "division": "Human Capital",
    "position": "Staff",
    "shift": "B",
    "employeeStatus": "Tetap",
    "chunkIndex": 12
  }
}
```

## 6. Workflow Upload / Ingestion

```text
User upload PDF/TXT
    |
    v
Backend validate file
    |
    v
Object Storage / MinIO menyimpan file asli
    |
    v
documents table menyimpan registry dokumen
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
Metadata extraction
    |
    v
Ollama embedding
    |
    v
Qdrant upsert vector + payload
    |
    v
document_chunks optional jika StorageMode.WriteDocumentChunksToPostgres=true
```

Service yang terlibat:

| Service | Tanggung jawab |
|---|---|
| `IngestionService` | Entry point upload PDF/TXT dari endpoint |
| `ObjectStorageService` | Menyimpan file asli ke MinIO dan memastikan bucket tersedia |
| `PdfTextExtractor` | Extract text dari PDF |
| `TextNormalizer` | Membersihkan dan menormalisasi text hasil extraction |
| `ChunkingService` | Memecah text menjadi chunks |
| `ChunkMetadataExtractor` | Extract metadata dari content chunk |
| `DocumentIngestionOrchestrator` | Mengatur flow ingestion end-to-end |
| `EmbeddingIngestionService` | Memanggil embedding model via Ollama |
| `QdrantPointWriter` | Upsert vector + payload lengkap ke Qdrant |
| `ChunkRepository` | Optional legacy write ke `document_chunks` jika storage mode aktif |

Structured data seperti karyawan, lembur, dan maintenance diproses menjadi 1 row = 1 chunk agar exact/filter retrieval akurat.

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
StructuredEntityResolver
    |  (Qdrant payload cache jika query butuh known entity)
    v
RetrievalService
    |
    +--> Qdrant payload filter
    |
    +--> Qdrant vector search with payload
    |
    v
AnswerFormatterService or PromptBuilderService
    |
    v
Ollama LLM jika diperlukan
    |
    v
ChatResponse
```

Penjelasan:

1. Frontend mengirim pertanyaan ke `/api/chat`.
2. `RagChatService` menjadi orchestrator utama.
3. `QueryAnalyzerService` menentukan intent, answer level, record type, dan policy marker.
4. `StructuredEntityResolver` membaca known entities dari cache Qdrant payload jika query mengandung entity natural.
5. `RetrievalService` memilih Qdrant payload filter atau Qdrant vector search.
6. `AnswerFormatterService` membuat jawaban deterministic jika data sudah structured.
7. Jika perlu LLM, `PromptBuilderService` membuat context grounded yang sudah dibersihkan dari noise/disclaimer.
8. `OllamaService` menjalankan LLM lokal.
9. Backend mengembalikan `ChatResponse`.

## 8. Three-Level Retrieval Strategy

### Level 1 - Exact/Structured Retrieval

Dipakai untuk data terstruktur:

- NIK
- maintenance code
- date
- name
- division
- shift
- employeeStatus
- position
- approval
- location
- equipment
- technician
- maintenanceStatus

Flow:

```text
QueryAnalyzerService
    |
    v
StructuredEntityResolver jika perlu
    |
    v
Qdrant payload filter
    |
    v
AnswerFormatterService
    |
    v
Response
```

Karakteristik:

- Menggunakan Qdrant payload filter.
- Tidak memakai LLM.
- Tidak memakai Supabase `document_chunks`.
- Cepat dan deterministic.

Contoh query:

- `Siapa karyawan dengan NIK RU6-1030?`
- `berikan saya data sinta lestari`
- `tampilkan karyawan divisi Keuangan`
- `tampilkan seluruh karyawan kontrak`
- `siapa aja yang shift C?`
- `Berikan data maintenance kode MT-308`
- `maintenance di gate utama ada gak?`
- `data valve control`

### Level 2 - Deterministic Template

Dipakai untuk pertanyaan pasti dan sering:

- SOP Keamanan Area Kilang
- backup server
- kepatuhan APD
- kecepatan kendaraan
- profil perusahaan
- audit/profile facts

Flow:

```text
Qdrant payload filter by recordType
    |
    v
AnswerFormatterService template
    |
    v
Response
```

Karakteristik:

- Menggunakan Qdrant payload.
- Tidak memakai LLM jika template cocok.
- Tidak memakai Supabase `document_chunks`.
- Mengurangi latency dan hallucination.

Contoh query:

- `Apa saja SOP Keamanan Area Kilang?`
- `Jam berapa backup otomatis server internal dilakukan?`
- `Apa tingkat kepatuhan APD?`
- `Berapa kecepatan maksimal kendaraan di area produksi?`
- `Apa nama unit perusahaan dalam dokumen ini?`
- `Berapa kapasitas produksi Pertamina RU VI Balongan?`

### Level 3 - Grounded Semantic/Policy Answer

Dipakai untuk pertanyaan fleksibel, interpretatif, policy/izin, prosedur, risiko, atau ringkasan.

Contoh:

- `Apakah orang IT boleh masuk area penyimpanan?`
- `Kalau pekerja selain HSSE ingin masuk area tangki, apakah diperbolehkan?`
- `Apa risiko membawa perangkat elektronik biasa ke area kilang?`
- `Bagaimana prosedur keamanan sebelum pekerja masuk area kilang?`
- `Ringkas isi dokumen ini dari sisi keamanan dan operasional.`
- `Apa informasi yang berkaitan dengan pengendalian risiko operasional perusahaan?`

Flow:

```text
Ollama embedding
    |
    v
Qdrant vector search with payload
    |
    v
Semantic context filtering/reranking
    |
    v
Clean context for LLM
    |
    v
PromptBuilderService grounded prompt
    |
    v
Ollama LLM
    |
    v
Response
```

Karakteristik:

- Menggunakan Qdrant vector search dengan payload.
- Context dibersihkan sebelum masuk LLM.
- LLM wajib menjawab hanya berdasarkan context.
- Prompt melarang LLM membuat izin, aturan, atau informasi baru.
- Jika context tidak cukup, jawaban harus:

```text
Maaf, saya tidak menemukan informasi tersebut.
```

## 9. Service Responsibilities

| Service / Class | Tanggung jawab |
|---|---|
| `RagChatService` | Orchestrator chat: analisis, retrieval, deterministic answer, prompt, LLM, response |
| `QueryAnalyzerService` | Mendeteksi intent, answer level, exact key, filter, record type, dan policy question |
| `StructuredEntityResolver` | Mencocokkan known entity dari Qdrant payload cache |
| `RetrievalService` | Memilih retrieval strategy dan mengambil context dari Qdrant payload/vector |
| `AnswerFormatterService` | Membuat deterministic answer untuk structured/profile/audit/SOP |
| `PromptBuilderService` | Membuat prompt grounded dan membersihkan context sebelum LLM |
| `OllamaService` | Client untuk embedding dan LLM lokal |
| `QdrantService` | Facade untuk operasi Qdrant agar caller stabil |
| `QdrantSearchClient` | Payload search, vector search, dan known entity retrieval via Qdrant |
| `QdrantScrollClient` | Scroll/pagination Qdrant untuk payload retrieval |
| `QdrantFilterBuilder` | Membuat filter payload Qdrant |
| `QdrantCollectionService` | Init collection dan payload index Qdrant |
| `QdrantPointWriter` | Upsert vector + payload lengkap ke Qdrant |
| `RetrievedChunkMapper` | Mapping response Qdrant payload/score ke `RetrievedChunk` |
| `ChunkMetadataExtractor` | Extract metadata dari chunk content |
| `ObjectStorageService` | Upload file asli ke MinIO |
| `IngestionService` | Entry point upload PDF/TXT |
| `DocumentIngestionOrchestrator` | Orkestrasi extraction, chunking, embedding, Qdrant upsert |
| `PdfTextExtractor` | Extract text dari PDF |
| `TextNormalizer` | Clean dan normalize text |
| `ChunkingService` | Split text menjadi chunks |
| `EmbeddingIngestionService` | Generate embedding untuk chunk |
| `ChunkRepository` | Legacy/debug repository untuk `document_chunks` jika storage mode aktif |

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

Form-data:

```text
key=file
type=File
```

Contoh:

```bash
curl -X POST http://localhost:5057/api/upload-pdf \
  -F "file=@Dummy_Data_Internal_Pertamina_RUVI_Balongan.pdf"
```

### `POST /api/upload-txt`

Upload file TXT.

Form-data:

```text
key=file
type=File
```

Contoh:

```bash
curl -X POST http://localhost:5057/api/upload-txt \
  -F "file=@data-internal.txt"
```

### `POST /api/ingest`

Ingest text langsung melalui JSON. Endpoint ini berguna untuk testing/debug.

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
  "Qdrant": {
    "BaseUrl": "http://localhost:6333",
    "CollectionName": "pertamina_chunks",
    "VectorSize": 768,
    "Distance": "Cosine"
  },
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text",
    "ChatModel": "qwen2.5:1.5b",
    "TimeoutSeconds": 120
  },
  "Retrieval": {
    "SemanticTopK": 5,
    "SemanticScoreThreshold": 0.55,
    "SemanticMaxContextChunks": 3,
    "StructuredDefaultLimit": 50
  },
  "ObjectStorage": {
    "Endpoint": "localhost:9000",
    "AccessKey": "minioadmin",
    "SecretKey": "minioadmin",
    "BucketName": "rag-documents",
    "UseSsl": false
  },
  "StorageMode": {
    "WriteDocumentChunksToPostgres": false
  }
}
```

Penjelasan:

| Config | Fungsi |
|---|---|
| `ConnectionStrings:SupabaseDb` | Koneksi PostgreSQL/Supabase untuk `documents` registry dan optional `document_chunks` |
| `Qdrant:BaseUrl` | Endpoint Qdrant |
| `Qdrant:CollectionName` | Nama collection Qdrant utama |
| `Qdrant:VectorSize` | Dimensi vector embedding |
| `Qdrant:Distance` | Distance metric collection, contoh `Cosine` |
| `Ollama:BaseUrl` | Endpoint Ollama |
| `Ollama:EmbeddingModel` | Model embedding lokal |
| `Ollama:ChatModel` | Model generation/chat lokal |
| `Ollama:TimeoutSeconds` | Timeout request Ollama |
| `Retrieval:SemanticTopK` | Jumlah kandidat awal semantic vector search |
| `Retrieval:SemanticScoreThreshold` | Minimum similarity semantic |
| `Retrieval:SemanticMaxContextChunks` | Maksimum chunk semantic yang dikirim ke LLM |
| `Retrieval:StructuredDefaultLimit` | Limit default untuk retrieval structured/filter |
| `ObjectStorage:Endpoint` | Endpoint MinIO, contoh `localhost:9000` |
| `ObjectStorage:AccessKey` | Access key MinIO |
| `ObjectStorage:SecretKey` | Secret key MinIO |
| `ObjectStorage:BucketName` | Bucket untuk file asli, contoh `rag-documents` |
| `ObjectStorage:UseSsl` | `false` untuk local HTTP, `true` untuk HTTPS |
| `StorageMode:WriteDocumentChunksToPostgres` | Mengaktifkan/menonaktifkan penulisan chunk ke PostgreSQL |

`WriteDocumentChunksToPostgres=false` berarti:

- `document_chunks` tidak diisi.
- Retrieval chat tetap berjalan dari Qdrant payload/vector.
- PostgreSQL tetap dipakai untuk registry `documents`.

`WriteDocumentChunksToPostgres=true` berarti:

- `document_chunks` tetap diisi sebagai backup/debug legacy.
- Query path chat tetap diarahkan ke Qdrant.

Catatan:

- Jangan commit secret production ke repository.
- Untuk production, gunakan environment variables atau secret manager.
- Semua konfigurasi di atas bisa dioverride dengan environment variable standar .NET, misalnya `Qdrant__BaseUrl`, `Qdrant__CollectionName`, `Ollama__BaseUrl`, `Ollama__EmbeddingModel`, `Ollama__ChatModel`, dan `StorageMode__WriteDocumentChunksToPostgres`.
- Pastikan Qdrant, MinIO, Ollama, dan PostgreSQL berjalan sebelum upload dokumen.

## 12. Installation & Running Locally

### 1. Clone repository

```bash
git clone <repository-url>
cd be_service
```

### 2. Install .NET SDK

Project saat ini menargetkan:

```text
net10.0
```

### 3. Jalankan Qdrant

```bash
docker run -p 6333:6333 qdrant/qdrant
```

### 4. Jalankan MinIO

```bash
docker run -d -p 9000:9000 -p 9001:9001 \
  --name minio \
  -e "MINIO_ROOT_USER=minioadmin" \
  -e "MINIO_ROOT_PASSWORD=minioadmin" \
  quay.io/minio/minio server /data --console-address ":9001"
```

MinIO console:

```text
http://localhost:9001
```

Login local:

```text
username: minioadmin
password: minioadmin
```

Bucket:

```text
rag-documents
```

Bucket akan dibuat otomatis oleh `ObjectStorageService` jika belum ada.

### 5. Jalankan Ollama

Pastikan Ollama berjalan di:

```text
http://localhost:11434
```

Command:

```bash
ollama serve
```

Pull model yang dipakai:

```bash
ollama pull nomic-embed-text
ollama pull qwen2.5:1.5b
```

### 6. Setup PostgreSQL/Supabase

Pastikan PostgreSQL/Supabase local berjalan dan table `documents` tersedia.

Untuk kolom object storage, jalankan:

```text
Sql/add_object_storage_columns.sql
```

Jika ingin tetap memakai `document_chunks` sebagai legacy/debug, pastikan table tersebut juga tersedia.

### 7. Restore dan build

```bash
dotnet restore
dotnet build
```

Jika `dotnet` tidak ada di PATH:

```bash
/usr/local/share/dotnet/dotnet build
```

### 8. Run backend

```bash
dotnet run
```

Default endpoint local:

```text
http://localhost:5057
```

### 9. Init Qdrant collection

```bash
curl http://localhost:5057/api/qdrant/init
```

### 10. Upload PDF

```bash
curl -X POST http://localhost:5057/api/upload-pdf \
  -F "file=@dokumen.pdf"
```

### 11. Upload TXT

```bash
curl -X POST http://localhost:5057/api/upload-txt \
  -F "file=@dokumen.txt"
```

### 12. Test chat

```bash
curl -X POST http://localhost:5057/api/chat \
  -H "Content-Type: application/json" \
  -d "{\"message\":\"Apa saja SOP Keamanan Area Kilang?\"}"
```

## 13. Database Setup

Schema minimal `documents`:

```sql
create table if not exists documents (
  id uuid primary key default gen_random_uuid(),
  title text not null,
  source_type text default 'text',
  file_name text,
  department text,
  storage_bucket text,
  storage_object_key text,
  content_type text,
  created_at timestamptz default now()
);
```

Migration object storage:

```sql
alter table documents add column if not exists storage_bucket text;
alter table documents add column if not exists storage_object_key text;
alter table documents add column if not exists content_type text;
```

File migration:

```text
Sql/add_object_storage_columns.sql
```

Schema optional/legacy `document_chunks`:

```sql
create extension if not exists vector;

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

`document_chunks` tidak wajib terisi jika:

```json
"WriteDocumentChunksToPostgres": false
```

## 14. Qdrant Setup

Collection Qdrant dibuat melalui:

```text
GET /api/qdrant/init
```

Konfigurasi utama:

- Collection: `pertamina_chunks`
- Distance: `Cosine`
- Point id: chunk id
- Payload: content + metadata lengkap

Qdrant point menyimpan:

```json
{
  "id": "chunk-guid",
  "vector": [0.01, 0.02, 0.03],
  "payload": {
    "content": "Isi chunk...",
    "documentId": "document-guid",
    "documentTitle": "Dokumen.pdf",
    "recordType": "sop",
    "sectionTitle": "SOP Keamanan Area Kilang",
    "chunkIndex": 1
  }
}
```

Qdrant digunakan untuk:

- Exact/structured lookup via payload filter.
- Generic recordType lookup via payload filter.
- SOP/audit/profile retrieval via payload filter.
- Semantic vector search with payload.
- Known entity source untuk `StructuredEntityResolver`.

Jika collection Qdrant dihapus, dokumen perlu di-upload/re-ingest ulang agar payload dan vector tersedia lagi.

## 15. Testing Checklist

### Level 1 - Exact/Structured Retrieval

- [ ] `Siapa karyawan dengan NIK RU6-1030?`
  - Expected: `source=qdrant_payload`, `usedLlm=false`

- [ ] `berikan saya data sinta lestari`
  - Expected: `source=qdrant_payload`, `usedLlm=false`

- [ ] `tampilkan karyawan divisi Keuangan`
  - Expected: `source=qdrant_payload`, `usedLlm=false`

- [ ] `maintenance di gate utama ada gak?`
  - Expected: `source=qdrant_payload`, `usedLlm=false`

- [ ] `berikan saya data karyawan`
  - Expected: `source=qdrant_payload`, `usedLlm=false`

### Level 2 - Deterministic Template

- [ ] `Apa saja SOP Keamanan Area Kilang?`
  - Expected: `source=qdrant_payload`, `usedLlm=false`

- [ ] `Jam berapa backup otomatis server internal dilakukan?`
  - Expected: `source=qdrant_payload`, `usedLlm=false`

- [ ] `Apa tingkat kepatuhan APD?`
  - Expected: `source=qdrant_payload`, `usedLlm=false`

- [ ] `Berapa kecepatan maksimal kendaraan di area produksi?`
  - Expected: `source=qdrant_payload`, `usedLlm=false`

### Level 3 - Grounded Semantic/Policy Answer

- [ ] `Apa informasi yang berkaitan dengan pengendalian risiko operasional perusahaan?`
  - Expected: `source=qdrant_vector_payload`, `usedLlm=true`

- [ ] `Apa risiko membawa perangkat elektronik biasa ke area kilang?`
  - Expected: grounded answer dari SOP/audit, tidak menyebut informasi di luar context

- [ ] `Ringkas isi dokumen ini dari sisi keamanan dan operasional.`
  - Expected: jawaban ringkas, context SOP/audit/profile, tidak membahas disclaimer dummy

- [ ] `Apakah orang IT boleh masuk area penyimpanan?`
  - Expected: policy answer grounded, tidak menyimpulkan safety briefing sebagai izin akses

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

Menjelaskan jalur retrieval.

Contoh:

```text
RETRIEVAL_TRACE answerLevel=SemanticGrounded mode=semantic source=qdrant_vector_payload qdrantVectorSearch=True chunks=3
```

Makna:

- `answerLevel`: Level 1/2/3 yang dipilih.
- `mode`: mode teknis retrieval.
- `source`: sumber retrieval.
- `qdrantVectorSearch`: apakah vector search Qdrant dipakai.
- `chunks`: jumlah chunk final.

### `ANSWER_TRACE`

Menjelaskan cara jawaban dibuat.

Contoh:

```text
ANSWER_TRACE answerLevel=DeterministicTemplate retrievalMode=sop source=qdrant_payload qdrantVectorSearch=False usedDeterministicAnswer=True usedLlm=False
```

Makna:

- `usedDeterministicAnswer=True`: jawaban dibuat oleh formatter.
- `usedLlm=True`: jawaban dibuat oleh Ollama LLM.

### `SEMANTIC_FILTER`

Menjelaskan filtering context semantic.

Contoh:

```text
SEMANTIC_FILTER before=5 after=3 allowedTypes=sop,audit,profile
```

### `STRUCTURED_ENTITY_RESOLVER`

Menjelaskan source known entities.

Contoh:

```text
STRUCTURED_ENTITY_RESOLVER source=qdrant_payload_cache count=120
STRUCTURED_ENTITY_RESOLVER source=qdrant_payload_refresh count=120
```

### `STRUCTURED_ENTITY_MATCH`

Menjelaskan hasil entity match tanpa membocorkan content panjang.

Contoh:

```text
STRUCTURED_ENTITY_MATCH field=name matchType=fuzzy score=0.96
```

### `INGESTION_STORAGE_MODE`

Menjelaskan apakah `document_chunks` ditulis.

Contoh:

```text
INGESTION_STORAGE_MODE writeDocumentChunksToPostgres=false
```

### `OBJECT_STORAGE_UPLOAD`

Menjelaskan upload file asli ke Object Storage.

Contoh:

```text
OBJECT_STORAGE_UPLOAD bucket=rag-documents objectKey=documents/... contentType=application/pdf
```

Jika log menunjukkan:

```text
source=qdrant_vector_payload
qdrantVectorSearch=True
```

artinya semantic search memakai Qdrant dan langsung menggunakan payload Qdrant sebagai context.

Jika log menunjukkan:

```text
source=qdrant_payload
qdrantVectorSearch=False
```

artinya retrieval memakai filter payload Qdrant tanpa vector search.

## 17. Current Limitations

Limitasi saat ini:

- MinIO wajib aktif untuk upload PDF/TXT.
- Qdrant menjadi retrieval store utama; jika collection dihapus, perlu re-ingest.
- `document_chunks` optional dan tidak selalu berisi data.
- Parser dan normalizer masih disesuaikan dengan format dokumen tertentu.
- LLM lokal masih bisa menghasilkan jawaban kurang natural, sehingga prompt dan evaluation perlu terus diperbaiki.
- Belum ada authentication/authorization.
- Belum ada ACL per user, role, atau department.
- Belum ada background job ingestion untuk dokumen besar.
- Belum ada automated RAG evaluation suite.
- Belum ada document versioning dan delete/reindex pipeline yang lengkap.
- Observability masih berbasis logging, belum ada dashboard metrics.

## 18. Production Roadmap

Roadmap menuju production-ready:

- Tambahkan authentication untuk API backend.
- Tambahkan authorization dan ACL per user/department/document.
- Tambahkan document versioning.
- Tambahkan delete/reindex pipeline untuk Object Storage, Qdrant, dan registry dokumen.
- Tambahkan background job queue untuk ingestion dokumen besar.
- Tambahkan retry, timeout, dan circuit breaker untuk Qdrant, MinIO, Ollama, dan PostgreSQL.
- Tambahkan monitoring latency, error rate, retrieval source, dan LLM usage.
- Tambahkan automated regression test dan RAG evaluation suite.
- Tambahkan rate limiting untuk endpoint chat/upload.
- Pindahkan secrets dan service URL ke environment variables atau secret manager.
- Tambahkan Docker Compose untuk backend, Qdrant, MinIO, PostgreSQL/Supabase, dan Ollama jika diperlukan.
- Tambahkan streaming response jika frontend membutuhkan pengalaman chat yang lebih responsif.
- Tambahkan frontend auth dan session management.

## 19. Project Status

Status project:

```text
Production-like prototype
```

Project saat ini sudah menggunakan Qdrant-centric chat retrieval:

- Source document asli disimpan di Object Storage/MinIO.
- Chunk retrieval utama berada di Qdrant payload/vector.
- PostgreSQL/Supabase dipakai untuk registry/admin metadata.
- `document_chunks` bersifat optional/legacy/debug.
- Structured entity detection membaca known entities dari Qdrant payload cache.
- Level 1 dan Level 2 tidak memakai LLM jika data/template cukup.
- Level 3 memakai Qdrant vector search with payload dan grounded prompt.

Project belum production final, tetapi arsitekturnya sudah mendekati target production-like RAG yang lebih sederhana di query path:

```text
User query -> Qdrant -> AnswerFormatter/PromptBuilder -> Ollama jika perlu -> ChatResponse
```

Sebelum dipakai sebagai sistem production penuh, project masih perlu security, ACL, reindex strategy, background ingestion, observability, automated evaluation, dan deployment hardening.
