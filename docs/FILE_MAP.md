# File Map Backend RAG

Dokumen ini adalah peta navigasi codebase backend RAG. Tujuannya bukan menggantikan README, tetapi membantu memahami file mana yang penting, dipanggil dari mana, dan terhubung ke service apa.

Project ini bisa dibaca lewat dua flow utama:

```text
Upload / Ingestion Flow
PDF/TXT -> extract text -> normalize -> chunk -> metadata -> embedding -> Qdrant

Chat Flow
User question -> query understanding -> retrieval -> formatter/prompt -> answer
```

## 1. Cara Membaca Project

Mulai dari file berikut:

| Urutan | File | Kenapa Dibaca |
|---|---|---|
| 1 | `Program.cs` | Melihat endpoint, dependency injection, config, dan middleware |
| 2 | `Services/RagChatService.cs` | Melihat alur utama chat dari pertanyaan sampai response |
| 3 | `Services/RetrievalService.cs` | Melihat bagaimana sistem memilih Qdrant payload search atau vector search |
| 4 | `Services/AnswerFormatterService.cs` | Melihat jawaban deterministic/tabel tanpa LLM |
| 5 | `Services/PromptBuilderService.cs` | Melihat prompt yang dikirim ke LLM saat semantic answer |
| 6 | `Services/IngestionService.cs` | Melihat entry point upload PDF/TXT |
| 7 | `Services/Ingestion/DocumentIngestionOrchestrator.cs` | Melihat flow ingestion end-to-end sampai Qdrant |
| 8 | `Services/QdrantService.cs` | Melihat facade utama ke Qdrant |

Kalau ingin debugging cepat, biasanya cukup mulai dari:

```text
Program.cs
-> RagChatService
-> QueryUnderstandingService / QueryAnalyzerService
-> RetrievalService
-> QdrantService
-> AnswerFormatterService / PromptBuilderService
```

## 2. High-Level Module Map

```text
Program.cs
  |
  +-- API endpoints
  |     +-- /api/chat
  |     +-- /api/ingest
  |     +-- /api/upload-pdf
  |     +-- /api/upload-txt
  |     +-- /api/qdrant/init
  |     +-- /api/qdrant/recreate
  |
  +-- Middleware
  |     +-- ApiKeyMiddleware
  |     +-- GlobalExceptionHandlingMiddleware
  |
  +-- Services
        +-- Chat services
        +-- Ingestion services
        +-- Qdrant services
        +-- Object storage
        +-- Ollama
        +-- Repository
```

## 3. Chat Flow

Endpoint:

```text
POST /api/chat
```

Flow aktual:

```text
Program.cs
  |
  v
RagChatService.AskAsync()
  |
  +-- QueryUnderstandingService.AnalyzeAsync()
  |     LLM-based query understanding, dipakai saat Rag.Mode=Semantic
  |
  +-- QueryAnalyzerService.AnalyzeAsync()
  |     Rule-based analyzer, dipakai sebagai fallback / fast path / legacy mode
  |
  +-- StructuredEntityResolver
  |     Cek known entity dari Qdrant payload cache
  |
  v
RetrievalService
  |
  +-- Qdrant payload filter
  |     Untuk exact/structured query
  |
  +-- Qdrant vector/hybrid search
        Untuk semantic/narrative query
  |
  v
AnswerFormatterService
  |
  +-- Jika deterministic answer tersedia, return tanpa LLM
  |
  v
PromptBuilderService
  |
  +-- Jika butuh LLM, build grounded prompt
  |
  v
OllamaService
  |
  v
ChatResponse
```

### File Chat Utama

| File | Tanggung Jawab | Dipanggil Oleh | Memanggil |
|---|---|---|---|
| `Services/RagChatService.cs` | Orchestrator chat utama | `Program.cs` endpoint `/api/chat` | `QueryUnderstandingService`, `QueryAnalyzerService`, `RetrievalService`, `AnswerFormatterService`, `PromptBuilderService`, `OllamaService` |
| `Services/QueryUnderstandingService.cs` | Memahami intent query dengan LLM JSON | `RagChatService` | `OllamaService`, `QueryAnalyzerService` fallback |
| `Services/QueryAnalyzerService.cs` | Analyzer rule-based untuk exact key, recordType, policy, fallback | `RagChatService`, `QueryUnderstandingService` | `FieldIntentClassifier`, `FieldSchema` |
| `Services/StructuredEntityResolver.cs` | Mencocokkan entity known value dari Qdrant payload cache | `RagChatService`, `RetrievalService` | `QdrantService` |
| `Services/RetrievalService.cs` | Memilih dan menjalankan retrieval strategy | `RagChatService` | `QdrantService`, `OllamaService`, `StructuredEntityResolver` |
| `Services/AnswerFormatterService.cs` | Membuat jawaban deterministic tanpa LLM | `RagChatService` | `ChunkMetadataExtractor`, helper internal |
| `Services/PromptBuilderService.cs` | Membuat prompt grounded untuk LLM | `RagChatService` | Helper internal |
| `Services/OllamaService.cs` | HTTP client untuk embedding, generate, dan query understanding | Banyak service | Ollama API |

## 4. Ingestion Flow

Endpoint:

```text
POST /api/upload-pdf
POST /api/upload-txt
POST /api/ingest
```

Flow upload PDF/TXT:

```text
Program.cs
  |
  v
IngestionService
  |
  +-- ObjectStorageService
  |     Upload file asli ke MinIO
  |
  +-- PdfTextExtractor / TXT reader
  |     Extract raw text
  |
  v
DocumentIngestionOrchestrator
  |
  +-- PostgreSQL documents registry
  |
  +-- TextNormalizer
  |     Normalize text
  |
  +-- ChunkingService
  |     Split text menjadi ContentChunk
  |
  +-- ChunkMetadataExtractor
  |     Detect recordType, sectionTitle, chunkType, dataset fields
  |
  +-- EmbeddingIngestionService
  |     Dense embedding via Ollama
  |     Sparse BM25 vector jika hybrid aktif
  |
  +-- QdrantService / QdrantPointWriter
        Upsert vector + payload ke Qdrant
```

### File Ingestion Utama

| File | Tanggung Jawab | Dipanggil Oleh | Memanggil |
|---|---|---|---|
| `Services/IngestionService.cs` | Entry point upload PDF/TXT dan ingest text | `Program.cs` upload/ingest endpoints | `ObjectStorageService`, `PdfTextExtractor`, `DocumentIngestionOrchestrator` |
| `Services/ObjectStorageService.cs` | Upload file asli ke MinIO dan generate object key | `IngestionService` | MinIO SDK |
| `Services/Ingestion/PdfTextExtractor.cs` | Extract text dari PDF | `IngestionService` | PdfPig / iText terkait extraction |
| `Services/Ingestion/DocumentIngestionOrchestrator.cs` | Orkestrasi ingestion end-to-end | `IngestionService` | PostgreSQL, `TextNormalizer`, `ChunkingService`, `EmbeddingIngestionService`, `QdrantService`, `ChunkRepository` |
| `Services/Ingestion/TextNormalizer.cs` | Normalisasi text hasil extraction | `DocumentIngestionOrchestrator` | Helper internal |
| `Services/Ingestion/ChunkingService.cs` | Split dokumen menjadi chunk structured/narrative | `DocumentIngestionOrchestrator` | `ChunkMetadataExtractor`, `DatasetSchemaOptions` |
| `Services/ChunkMetadataExtractor.cs` | Detect recordType, sectionTitle, chunkType, dan field dataset | `DocumentIngestionOrchestrator`, `ChunkingService`, formatter | Regex/helper internal |
| `Services/Ingestion/EmbeddingIngestionService.cs` | Generate dense embedding dan sparse vector | `DocumentIngestionOrchestrator` | `OllamaService`, `SparseBm25Encoder` |
| `Repositories/ChunkRepository.cs` | Optional legacy write ke PostgreSQL `document_chunks` | `DocumentIngestionOrchestrator` | PostgreSQL |

## 5. Qdrant Layer

Qdrant layer dipisah agar logic HTTP/SDK Qdrant tidak bercampur dengan chat dan ingestion.

```text
QdrantService
  |
  +-- QdrantCollectionService
  +-- QdrantPointWriter
  +-- QdrantSearchClient
  +-- QdrantScrollClient
  +-- QdrantFilterBuilder
  +-- RetrievedChunkMapper
```

| File | Tanggung Jawab | Dipanggil Oleh |
|---|---|---|
| `Services/QdrantService.cs` | Facade utama untuk operasi Qdrant | `RetrievalService`, `DocumentIngestionOrchestrator`, endpoints init/recreate |
| `Services/Qdrant/QdrantCollectionService.cs` | Ensure/recreate collection, named vectors, payload indexes | `QdrantService` |
| `Services/Qdrant/QdrantPointWriter.cs` | Upsert chunk vector + payload ke Qdrant | `QdrantService` |
| `Services/Qdrant/QdrantSearchClient.cs` | Payload search, dense vector search, hybrid dense+sparse search | `QdrantService` |
| `Services/Qdrant/QdrantScrollClient.cs` | Scroll Qdrant payload, termasuk known entity source | `QdrantService` |
| `Services/Qdrant/QdrantFilterBuilder.cs` | Build Qdrant payload filters | `QdrantSearchClient`, `QdrantScrollClient` |
| `Services/Qdrant/QdrantConstants.cs` | Nama vector field dan konstanta Qdrant | Qdrant layer |
| `Services/RetrievedChunkMapper.cs` | Mapping payload Qdrant menjadi `RetrievedChunk` | Qdrant clients |
| `Services/SparseBm25Encoder.cs` | Membuat sparse vector BM25 lokal | `EmbeddingIngestionService`, `RetrievalService` |

## 6. Models Map

| File | Fungsi |
|---|---|
| `Models/ChatRequest.cs` | DTO request `/api/chat` |
| `Models/ChatResponse.cs` | DTO response `/api/chat` |
| `Models/IngestRequest.cs` | DTO request ingestion internal |
| `Models/RagQueryAnalysis.cs` | Hasil analisis query: intent, entity, answerLevel, block reason, target field |
| `Models/RagRetrievalResult.cs` | Hasil retrieval: chunks, mode, source, answerLevel |
| `Models/RetrievedChunk.cs` | Model chunk hasil Qdrant/Postgres legacy |
| `Models/ContentChunk.cs` | Model chunk mentah hasil `ChunkingService` sebelum embedding |
| `Models/StructuredEntityMatch.cs` | Known entity value dari Qdrant payload |
| `Models/AnswerLevel.cs` | Enum level jawaban: blocked, exact, deterministic, semantic, policy |
| `Models/DatasetSchemaOptions.cs` | Config schema dataset: recordType, field, delimiter, index |
| `Models/QdrantOptions.cs` | Config Qdrant |
| `Models/OllamaOptions.cs` | Config Ollama |
| `Models/RetrievalOptions.cs` | Config retrieval topK, threshold, hybrid |
| `Models/RagModeOptions.cs` | Config mode RAG: Legacy/Hybrid/Semantic |
| `Models/ObjectStorageOptions.cs` | Config MinIO/Object Storage |
| `Models/StorageModeOptions.cs` | Config optional write `document_chunks` |
| `Models/FieldIntentResult.cs` | Hasil klasifikasi field intent |

## 7. Middleware dan Error Handling

| File | Fungsi | Catatan |
|---|---|---|
| `Services/ApiKeyMiddleware.cs` | Validasi header `X-API-Key` | API key sederhana, belum user auth/ACL |
| `Services/GlobalExceptionHandlingMiddleware.cs` | Response error aman untuk exception umum/dependency | Menghindari stacktrace mentah ke client |
| `Services/ExternalServiceUnavailableException.cs` | Exception untuk Qdrant/Ollama/MinIO unavailable | Dipakai global exception handler |
| `Services/UpstreamServiceException.cs` | Exception untuk upstream response bermasalah | Dipakai client service |
| `Services/HttpResponseGuard.cs` | Helper validasi response HTTP upstream | Dipakai service HTTP |
| `Services/HealthCheckService.cs` | Service cek dependency | Ada service, tetapi endpoint health perlu dicek di `Program.cs` |

## 8. Configuration dan Options

Config utama ada di `appsettings.Example.json` dan bisa dioverride via environment variable standar .NET.

| Config | Dipakai Oleh |
|---|---|
| `ConnectionStrings:SupabaseDb` | `DocumentIngestionOrchestrator`, `ChunkRepository`, health check |
| `Qdrant` | Qdrant collection/search/upsert clients |
| `Ollama` | `OllamaService` |
| `Retrieval` | `RetrievalService`, `EmbeddingIngestionService`, `QdrantService` |
| `ObjectStorage` | `ObjectStorageService` |
| `StorageMode` | `DocumentIngestionOrchestrator` |
| `Rag` | `RagChatService` |
| `DatasetSchema` | `ChunkingService`, `ChunkMetadataExtractor`, Qdrant payload indexes |
| `Security` | `ApiKeyMiddleware` |

## 9. Core RAG vs Domain-Specific Logic

### Core RAG yang relatif aman dipertahankan

| Area | File |
|---|---|
| API orchestration | `Program.cs`, `RagChatService.cs` |
| Object storage | `ObjectStorageService.cs` |
| PDF/TXT ingestion | `IngestionService.cs`, `PdfTextExtractor.cs` |
| Generic chunk model | `ContentChunk.cs`, `RetrievedChunk.cs` |
| Embedding | `EmbeddingIngestionService.cs`, `OllamaService.cs` |
| Qdrant storage/retrieval | `QdrantService.cs`, `Services/Qdrant/*`, `RetrievedChunkMapper.cs` |
| Grounded prompt generic | `PromptBuilderService.BuildSemanticPrompt` |
| Error handling | `GlobalExceptionHandlingMiddleware.cs`, `ExternalServiceUnavailableException.cs` |

### Domain-specific / dummy-adapter logic

| Area | File | Contoh Hardcode |
|---|---|---|
| Legacy query routing | `QueryAnalyzerService.cs` | employee, overtime, maintenance, SOP kilang, RU6, MT |
| LLM query understanding prompt | `QueryUnderstandingService.cs` | intent employee/overtime/maintenance, valid values dummy |
| Structured formatter | `AnswerFormatterService.cs` | tabel karyawan, lembur, maintenance, SOP Pertamina |
| Text normalization | `TextNormalizer.cs` | format dummy karyawan/lembur/maintenance |
| RecordType detection | `ChunkMetadataExtractor.cs` | Data Karyawan, Rekap Lembur, Log Maintenance, SOP Keamanan |
| Dataset default | `DatasetSchemaOptions.cs` | default schema Pertamina/dummy |
| Known structured entities | `StructuredEntityResolver.cs`, `QdrantScrollClient.cs` | name, division, shift, approval, equipment, technician |

Catatan: domain-specific logic tidak harus dihapus sekarang. Arah yang lebih aman adalah memisahkannya sebagai domain adapter agar core RAG tetap bisa dipakai untuk dataset final.

## 10. Jika Chat Salah Jawab, Mulai Debug dari Mana

### Kasus: query masuk route yang salah

Contoh:

```text
Apa yang harus dilakukan panitia saat keadaan darurat event?
```

Mulai cek:

```text
RagChatService
-> QueryUnderstandingService
-> ApplyIntentSanityGate
-> RetrievalService
```

Log yang dicari:

```text
QUS_TRACE
QUS_VS_LEGACY
GROUNDING_GATE
RETRIEVAL_TRACE
ANSWER_TRACE
```

### Kasus: structured query mengambil terlalu banyak data

Contoh:

```text
Sinta Lestari bekerja di divisi apa?
```

Mulai cek:

```text
QueryUnderstandingService / QueryAnalyzerService
-> RagQueryAnalysis
-> RetrievalService.SearchByNameAsync
-> AnswerFormatterService
```

Masalah biasanya:

- Entity terdeteksi, tetapi requested field belum dipakai dengan tegas.
- Retrieval by name mengambil employee + overtime.
- Formatter menampilkan full record, bukan field-only answer.

### Kasus: semantic query tidak menemukan jawaban

Mulai cek:

```text
RetrievalService.SearchSemanticOnlyAsync
-> OllamaService.GenerateEmbeddingAsync
-> QdrantSearchClient.SearchSemanticAsync
-> PromptBuilderService.BuildSemanticPrompt
```

Hal yang dicek:

- Apakah dokumen sudah ter-ingest ulang setelah perubahan chunking.
- Apakah payload Qdrant berisi `content`.
- Apakah `recordType`, `chunkType`, dan `sectionTitle` benar.
- Apakah threshold retrieval terlalu tinggi.

### Kasus: upload berhasil tapi chunk sedikit/aneh

Mulai cek:

```text
PdfTextExtractor
-> TextNormalizer
-> ChunkingService
-> ChunkMetadataExtractor
-> QdrantPointWriter
```

Hal yang dicek:

- Apakah teks PDF berhasil diekstrak.
- Apakah heading section dikenali.
- Apakah dokumen structured atau narrative.
- Apakah payload Qdrant punya `chunkType` dan `sectionTitle`.

## 11. File yang Wajib Dipahami untuk Presentasi

| File | Tingkat Penting | Penjelasan Sederhana |
|---|---|---|
| `Program.cs` | Wajib | Endpoint dan daftar service |
| `RagChatService.cs` | Wajib | Otak flow chat |
| `RetrievalService.cs` | Wajib | Memilih data dari Qdrant |
| `QdrantService.cs` | Wajib | Jembatan backend ke Qdrant |
| `IngestionService.cs` | Wajib | Entry upload dokumen |
| `DocumentIngestionOrchestrator.cs` | Wajib | Pipeline dokumen sampai Qdrant |
| `ChunkingService.cs` | Wajib | Cara dokumen dipotong menjadi chunk |
| `PromptBuilderService.cs` | Wajib | Cara context dibuat untuk LLM |
| `AnswerFormatterService.cs` | Wajib | Jawaban cepat tanpa LLM |

## 12. File yang Wajib Dipahami untuk Debugging

| File | Dipakai Saat |
|---|---|
| `QueryUnderstandingService.cs` | Query salah intent |
| `QueryAnalyzerService.cs` | Fast path/legacy route salah |
| `StructuredEntityResolver.cs` | Entity typo/name/division/location tidak ketemu |
| `QdrantSearchClient.cs` | Payload/vector search salah |
| `QdrantScrollClient.cs` | Known entity cache salah |
| `RetrievedChunkMapper.cs` | Payload Qdrant tidak kebaca ke model |
| `ChunkMetadataExtractor.cs` | RecordType/field salah |
| `TextNormalizer.cs` | Teks PDF kacau sebelum chunking |
| `GlobalExceptionHandlingMiddleware.cs` | Error dependency tidak rapi |

## 13. File yang Bisa Dipelajari Nanti

| File | Alasan Bisa Nanti |
|---|---|
| `QdrantFilterBuilder.cs` | Detail teknis filter Qdrant |
| `QdrantConstants.cs` | Konstanta kecil |
| `RetrievalModes.cs` | Konstanta retrieval mode |
| `HttpResponseGuard.cs` | Helper response HTTP |
| `SparseBm25Encoder.cs` | Detail hybrid sparse vector |
| `FieldIntentClassifier.cs` | Tambahan untuk field/sensitive intent |
| `ChunkRepository.cs` | Legacy optional `document_chunks` |

## 14. Ringkasan Mental Model

Gunakan ringkasan ini saat menjelaskan project:

```text
Program.cs
  = daftar endpoint dan service

RagChatService
  = pengatur alur chat

QueryUnderstandingService / QueryAnalyzerService
  = memahami pertanyaan user

RetrievalService
  = memilih dan mengambil chunk dari Qdrant

AnswerFormatterService
  = membuat jawaban deterministic cepat

PromptBuilderService + OllamaService
  = membuat jawaban grounded kalau butuh LLM

IngestionService + DocumentIngestionOrchestrator
  = upload dokumen sampai masuk Qdrant

QdrantService + Qdrant clients
  = semua operasi teknis ke Qdrant
```

Kalau hanya ingin paham arsitektur untuk demo, fokus pada flow. Kalau ingin memperbaiki bug, fokus pada log `QUS_TRACE`, `RETRIEVAL_TRACE`, dan `ANSWER_TRACE`.
