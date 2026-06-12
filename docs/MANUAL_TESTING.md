# Manual Testing Guide

Panduan ini digunakan untuk menguji backend RAG secara manual setelah dependency lokal berjalan. Fokus pengujian adalah upload dokumen, Object Storage, Qdrant payload/vector retrieval, chat regression, logging, dan error handling dependency.

> **Catatan pembaruan (2026-06-11) — baca dulu, mengoreksi beberapa bagian di bawah:**
> 1. **Semua endpoint kini wajib header `X-API-Key`** (env var `Security__ApiKey` / `appsettings.json`).
>    Tambahkan `-H "X-API-Key: <key>"` pada semua contoh `curl` di bawah.
> 2. **Endpoint `GET /health` dan `GET /api/qdrant/status` BELUM terpasang** di build saat ini.
>    Endpoint nyata: `POST /api/chat`, `POST /api/ingest`, `POST /api/upload-pdf`, `POST /api/upload-txt`,
>    `GET /api/qdrant/init`, `POST /api/qdrant/recreate`. Abaikan Seksi 6 & 7 sampai endpoint itu dibuat;
>    verifikasi Qdrant lewat scroll langsung (Seksi 10) atau `GET /api/qdrant/init`.
> 3. Mode aktif **Semantic** (`Rag.Mode`); rahasia via env var. Untuk ingest bersih (tanpa duplikat)
>    gunakan `POST /api/qdrant/recreate` lalu upload ulang.

## 1. Prerequisites

Pastikan service berikut aktif:

- Backend .NET: `http://localhost:5057`
- Qdrant: `http://localhost:6333`
- Ollama: `http://localhost:11434`
- MinIO API: `http://localhost:9000`
- MinIO Console: `http://localhost:9001`
- Supabase/PostgreSQL local

Arsitektur saat ini:

- Qdrant adalah retrieval store utama untuk vector + chunk content + metadata payload.
- MinIO/Object Storage menyimpan file asli PDF/TXT.
- PostgreSQL/Supabase menyimpan document registry/admin metadata.
- `document_chunks` bersifat optional/legacy/debug.
- Ollama digunakan untuk embedding dan LLM.

## 2. Start Dependencies

### Qdrant

```bash
docker run -p 6333:6333 qdrant/qdrant
```

### MinIO

```bash
docker run -d -p 9000:9000 -p 9001:9001 \
  --name minio \
  -e "MINIO_ROOT_USER=minioadmin" \
  -e "MINIO_ROOT_PASSWORD=minioadmin" \
  quay.io/minio/minio server /data --console-address ":9001"
```

MinIO:

- Console: `http://localhost:9001`
- Login: `minioadmin` / `minioadmin`
- Bucket: `rag-documents`

### Ollama

Pastikan Ollama berjalan:

```bash
ollama serve
```

Endpoint:

```text
http://localhost:11434
```

Pastikan model yang dipakai backend sudah tersedia, misalnya:

```bash
ollama pull nomic-embed-text
ollama pull qwen2.5:1.5b
```

### Backend

```bash
dotnet run
```

Jika `dotnet` tidak ada di PATH:

```bash
/usr/local/share/dotnet/dotnet run
```

## 3. Reset Test Data

Reset data hanya untuk pengujian lokal. Jangan jalankan command ini pada environment production.

### Qdrant

```bash
curl -X DELETE http://localhost:6333/collections/pertamina_chunks
curl http://localhost:5057/api/qdrant/init
```

### Supabase/PostgreSQL

SQL aman untuk reset isi tabel:

```sql
delete from document_chunks;
delete from documents;
```

Jika butuh reset identity/cascade:

```sql
truncate table document_chunks restart identity cascade;
truncate table documents restart identity cascade;
```

### MinIO

Opsi 1:

- Buka `http://localhost:9001`
- Masuk dengan `minioadmin` / `minioadmin`
- Buka bucket `rag-documents`
- Hapus semua object

Opsi 2 jika data MinIO tidak persistent:

```bash
docker stop minio
docker rm minio
```

Lalu jalankan ulang command MinIO pada bagian Start Dependencies.

## 4. Database Migration

Jalankan migration object storage:

```text
Sql/add_object_storage_columns.sql
```

Kolom yang ditambahkan ke tabel `documents`:

- `storage_bucket`
- `storage_object_key`
- `content_type`

SQL:

```sql
alter table documents add column if not exists storage_bucket text;
alter table documents add column if not exists storage_object_key text;
alter table documents add column if not exists content_type text;
```

## 5. Configuration Check

Pastikan `appsettings.json` memiliki konfigurasi berikut:

```json
{
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
    "SemanticTopK": 15,
    "SemanticScoreThreshold": 0.55,
    "SemanticMaxContextChunks": 5,
    "StructuredDefaultLimit": 50,
    "HybridSearchEnabled": true
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
  },
  "Rag": {
    "Mode": "Semantic",
    "ShadowCompare": false
  },
  "Security": {
    "ApiKey": "CHANGE_ME (atau via env var Security__ApiKey)"
  }
}
```

> Nilai non-rahasia di atas adalah default aktual. `HybridSearchEnabled=true` mengaktifkan
> sparse BM25 + RRF. `Rag.Mode=Semantic` mengaktifkan analyzer LLM + guard. Rahasia
> (`Security:ApiKey`, password DB, kunci MinIO) sebaiknya via environment variable.

Expected behavior:

- `Qdrant.CollectionName=pertamina_chunks`: semua retrieval Qdrant membaca collection yang sama.
- `Ollama.EmbeddingModel=nomic-embed-text`: embedding upload dan semantic query memakai model yang sama.
- `Retrieval.SemanticScoreThreshold=0.55`: semantic context dengan score rendah difilter.
- `WriteDocumentChunksToPostgres=false`: `document_chunks` tidak diisi. Chat tetap berjalan dari Qdrant payload/vector.
- `WriteDocumentChunksToPostgres=true`: `document_chunks` diisi sebagai backup/debug legacy.

Config bisa dioverride via environment variable standar .NET:

```text
Qdrant__BaseUrl
Qdrant__CollectionName
Ollama__BaseUrl
Ollama__EmbeddingModel
Ollama__ChatModel
StorageMode__WriteDocumentChunksToPostgres
```

## 6. Health Check

Endpoint:

```http
GET http://localhost:5057/health
```

Contoh:

```bash
curl http://localhost:5057/health
```

Expected:

- `status` bernilai `ok` jika semua dependency bisa diakses.
- `status` bernilai `degraded` jika salah satu dependency bermasalah.
- Dependency yang dicek: Qdrant, Ollama, MinIO, database.

Contoh response sukses:

```json
{
  "status": "ok",
  "service": "be_service",
  "timestamp": "2026-05-26T10:00:00.0000000+00:00",
  "dependencies": {
    "qdrant": "ok",
    "ollama": "ok",
    "minio": "ok",
    "database": "ok"
  }
}
```

## 7. Qdrant Status Check

Endpoint:

```http
GET http://localhost:5057/api/qdrant/status
```

Contoh:

```bash
curl http://localhost:5057/api/qdrant/status
```

Expected:

- `status = ok`
- `collectionName = pertamina_chunks`
- `pointsCount > 0` setelah upload dokumen

Jika collection belum ada:

```bash
curl http://localhost:5057/api/qdrant/init
```

Contoh response:

```json
{
  "status": "ok",
  "collectionName": "pertamina_chunks",
  "message": "Qdrant dapat dihubungi dan collection tersedia.",
  "pointsCount": 120,
  "vectorsCount": 120
}
```

## 8. Upload Test

### PDF

Endpoint:

```http
POST http://localhost:5057/api/upload-pdf
```

Body form-data:

```text
key=file
type=File
```

Contoh:

```bash
curl -X POST http://localhost:5057/api/upload-pdf \
  -F "file=@Dummy_Data_Internal_Pertamina_RUVI_Balongan.pdf"
```

### TXT

Endpoint:

```http
POST http://localhost:5057/api/upload-txt
```

Body form-data:

```text
key=file
type=File
```

Contoh:

```bash
curl -X POST http://localhost:5057/api/upload-txt \
  -F "file=@data-internal.txt"
```

Expected:

- File asli muncul di MinIO bucket `rag-documents`.
- Tabel `documents` terisi.
- Tabel `document_chunks` tetap kosong jika `WriteDocumentChunksToPostgres=false`.
- Qdrant collection `pertamina_chunks` berisi points dengan payload.

## 9. Verify MinIO

Buka:

```text
http://localhost:9001
```

Expected:

- Bucket `rag-documents` tersedia.
- Bucket berisi object key dengan format:

```text
documents/{yyyy}/{MM}/{guid}-{safeFileName}
```

Contoh:

```text
documents/2026/05/0d1f4c4b-9d6a-4c19-b8a8-dokumen.pdf
```

## 10. Verify Qdrant Payload

Command:

```bash
curl -X POST http://localhost:6333/collections/pertamina_chunks/points/scroll \
  -H "Content-Type: application/json" \
  -d '{
    "limit": 1,
    "with_payload": true,
    "with_vector": false
  }'
```

Expected payload keys:

- `content`
- `documentId`
- `documentTitle`
- `recordType`
- `chunkIndex`
- metadata fields seperti:
  - `nik`
  - `name`
  - `division`
  - `maintenanceCode`
  - `location`
  - `equipment`

Contoh potongan response:

```json
{
  "payload": {
    "content": "Data Karyawan: ...",
    "documentId": "00000000-0000-0000-0000-000000000000",
    "documentTitle": "Dummy_Data_Internal_Pertamina_RUVI_Balongan.pdf",
    "recordType": "employee",
    "chunkIndex": 12,
    "nik": "RU6-1030",
    "name": "Budi Santoso",
    "division": "Human Capital"
  }
}
```

## 11. Verify Supabase Tables

SQL:

```sql
select count(*) from documents;
select count(*) from document_chunks;
```

Expected jika `StorageMode.WriteDocumentChunksToPostgres=false`:

```text
documents > 0
document_chunks = 0
```

Expected jika `StorageMode.WriteDocumentChunksToPostgres=true`:

```text
documents > 0
document_chunks > 0
```

## 12. Chat Regression Tests

Gunakan endpoint:

```http
POST http://localhost:5057/api/chat
```

Body:

```json
{
  "message": "Apa saja SOP Keamanan Area Kilang?"
}
```

| No | Query | Expected Mode | Expected Source | Expected LLM | Expected Result |
|---|---|---|---|---|---|
| 1 | `Apa saja SOP Keamanan Area Kilang?` | `sop_general` | `qdrant_payload` | `false` | Menampilkan daftar SOP keamanan. |
| 2 | `Siapa karyawan dengan NIK RU6-1030?` | `exact-nik` | `qdrant_payload` | `false` | Menampilkan data karyawan sesuai NIK. |
| 3 | `RU6-9999` | `exact-nik` | `qdrant_payload` | `false` | Not found. |
| 4 | `Berikan data maintenance kode MT-308` | `exact-maintenance-code` | `qdrant_payload` | `false` | Menampilkan log maintenance MT-308. |
| 5 | `berikan saya data sinta lestari` | `exact-name` | `qdrant_payload` | `false` | Menampilkan semua data Sinta Lestari yang ditemukan. |
| 6 | `tampilkan karyawan divisi Keuangan` | `employee_by_division` | `qdrant_payload` | `false` | Menampilkan tabel karyawan divisi Keuangan. |
| 7 | `maintenance di gate utama ada gak?` | `maintenance_by_location` | `qdrant_payload` | `false` | Menampilkan maintenance di Gate Utama. |
| 8 | `data valve control` | `maintenance_by_equipment` | `qdrant_payload` | `false` | Menampilkan data maintenance Valve Control. |
| 9 | `berikan saya data karyawan` | `employee_general` | `qdrant_payload` | `false` | Menampilkan tabel data karyawan. |
| 10 | `Apa informasi yang berkaitan dengan pengendalian risiko operasional perusahaan?` | `semantic` | `qdrant_vector_payload` | `true` | Jawaban grounded dari SOP/audit, tanpa disclaimer dummy. |
| 11 | `Apa itu anjing?` | not found | - | `false` atau `true` sesuai routing | Tidak hallucinate. Jawab tidak menemukan informasi jika context tidak relevan. |

Contoh curl:

```bash
curl -X POST http://localhost:5057/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message":"Siapa karyawan dengan NIK RU6-1030?"}'
```

## 13. Expected Logs

Log yang perlu diperhatikan:

- `RETRIEVAL_TRACE`
- `ANSWER_TRACE`
- `SEMANTIC_FILTER`
- `STRUCTURED_ENTITY_RESOLVER`
- `STRUCTURED_ENTITY_MATCH`
- `INGESTION_STORAGE_MODE`
- `OBJECT_STORAGE_UPLOAD`
- `SERVICE_UNAVAILABLE`

Exact structured example:

```text
RETRIEVAL_TRACE answerLevel=ExactStructured, mode=exact-nik, source=qdrant_payload, qdrantVectorSearch=False
ANSWER_TRACE answerLevel=ExactStructured, retrievalMode=exact-nik, source=qdrant_payload, qdrantVectorSearch=False, usedDeterministicAnswer=True, usedLlm=False
```

Semantic example:

```text
RETRIEVAL_TRACE answerLevel=SemanticGrounded, mode=semantic, source=qdrant_vector_payload, qdrantVectorSearch=True
SEMANTIC_FILTER before=5 after=3 allowedTypes=audit,sop
ANSWER_TRACE answerLevel=SemanticGrounded, retrievalMode=semantic, source=qdrant_vector_payload, qdrantVectorSearch=True, usedDeterministicAnswer=False, usedLlm=True
```

Ingestion example:

```text
INGESTION_STORAGE_MODE writeDocumentChunksToPostgres=false
OBJECT_STORAGE_UPLOAD bucket=rag-documents objectKey=documents/... contentType=application/pdf
```

Dependency error example:

```text
SERVICE_UNAVAILABLE service=Qdrant
SERVICE_UNAVAILABLE service=Ollama
SERVICE_UNAVAILABLE service=ObjectStorage
```

## 14. Error Handling Tests

### Qdrant Mati

Stop Qdrant container:

```bash
docker stop <qdrant-container-name>
```

Test:

```bash
curl -i http://localhost:5057/api/qdrant/status
```

Expected HTTP `503`:

```json
{
  "status": "error",
  "collectionName": "pertamina_chunks",
  "message": "Qdrant tidak dapat dihubungi. Pastikan Qdrant berjalan di localhost:6333.",
  "pointsCount": null,
  "vectorsCount": null
}
```

Chat ketika Qdrant mati:

```bash
curl -i -X POST http://localhost:5057/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message":"Siapa karyawan dengan NIK RU6-1030?"}'
```

Expected HTTP `503`:

```json
{
  "error": "Qdrant unavailable",
  "message": "Layanan Qdrant tidak dapat dihubungi. Pastikan Qdrant berjalan di localhost:6333."
}
```

### Ollama Mati

Stop Ollama process, lalu jalankan semantic chat atau upload yang butuh embedding:

```bash
curl -i -X POST http://localhost:5057/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message":"Apa informasi yang berkaitan dengan pengendalian risiko operasional perusahaan?"}'
```

Expected HTTP `503`:

```json
{
  "error": "Ollama unavailable",
  "message": "Layanan Ollama tidak dapat dihubungi. Pastikan Ollama berjalan di localhost:11434."
}
```

### MinIO Mati

Stop MinIO container:

```bash
docker stop minio
```

Test upload:

```bash
curl -i -X POST http://localhost:5057/api/upload-pdf \
  -F "file=@dokumen.pdf"
```

Expected HTTP `503`:

```json
{
  "error": "Object storage unavailable",
  "message": "Object storage tidak dapat dihubungi. Pastikan MinIO berjalan dan bucket tersedia."
}
```

## 15. Troubleshooting

### Qdrant points kosong

Kemungkinan:

- Collection baru dibuat tetapi dokumen belum di-upload ulang.
- Qdrant collection pernah dihapus.

Solusi:

```bash
curl http://localhost:5057/api/qdrant/init
```

Lalu upload ulang dokumen.

### Payload kosong

Kemungkinan data lama masih berasal dari fase Qdrant vector-only.

Solusi:

- Hapus collection Qdrant.
- Jalankan `/api/qdrant/init`.
- Upload ulang dokumen agar payload lengkap tersimpan.

### MinIO bucket missing

Solusi:

- Buka `http://localhost:9001`.
- Buat bucket `rag-documents`.
- Atau upload ulang melalui backend; service akan mencoba membuat bucket jika belum ada.

### `document_chunks` masih terisi

Cek konfigurasi:

```json
"StorageMode": {
  "WriteDocumentChunksToPostgres": false
}
```

Jika masih terisi, kemungkinan config runtime berbeda dari file yang dicek.

### Ollama lambat

Semantic query bisa memakan waktu 10-40 detik tergantung:

- model lokal
- CPU/GPU
- ukuran context
- kondisi mesin

Exact/filter query harus jauh lebih cepat karena tidak memakai LLM.

### `dotnet` tidak di PATH

Gunakan:

```bash
/usr/local/share/dotnet/dotnet build
/usr/local/share/dotnet/dotnet run
```

## 16. Pass Criteria

Project dianggap lolos manual test jika:

- Upload menyimpan file asli ke MinIO.
- Qdrant memiliki points + payload lengkap.
- Tabel `documents` terisi.
- Tabel `document_chunks` kosong ketika `StorageMode.WriteDocumentChunksToPostgres=false`.
- Exact/filter query menghasilkan `source=qdrant_payload`.
- Generic recordType query menghasilkan `source=qdrant_payload`.
- Semantic query menghasilkan `source=qdrant_vector_payload`.
- Deterministic query tidak memakai LLM jika template tersedia.
- Semantic/policy query memakai LLM hanya saat diperlukan.
- Negative query tidak hallucinate.
- Error dependency menghasilkan HTTP `503` dengan response JSON yang jelas.
