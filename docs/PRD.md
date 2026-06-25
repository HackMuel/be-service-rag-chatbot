# Product Requirements Document (PRD)
## Internal RAG Chatbot Backend — be_service

| | |
|---|---|
| **Produk** | Backend Chatbot RAG Internal (be_service) |
| **Versi dokumen** | 1.1 |
| **Tanggal** | 2026-06-25 |
| **Status** | Production-like prototype (Clean Architecture 3-layer) |
| **Branch** | `CleanArchitecture` |
| **Penulis** | Samuel P |
| **Reviewer** | Pembimbing Magang |

---

## 1. Ringkasan Eksekutif

`be_service` adalah backend chatbot **Retrieval-Augmented Generation (RAG)** untuk menjawab
pertanyaan karyawan berdasarkan **dokumen internal** (PDF/TXT), bukan berdasarkan pengetahuan
bebas model bahasa. Sistem dirancang **Qdrant-centric**: file asli disimpan di Object Storage
(MinIO), potongan (chunk) konten + metadata disimpan sebagai payload di Qdrant, dan
PostgreSQL/Supabase berperan sebagai *document registry*.

Sistem menggabungkan dua kekuatan:
1. **Retrieval terstruktur** (filter payload eksak) untuk data tabular — cepat & deterministik.
2. **Retrieval semantik hybrid** (dense + sparse BM25, fusi RRF) untuk pertanyaan naratif.

Pemilihan jalur dikendalikan **lapisan pemahaman query berbasis LLM** dengan beberapa
*guard* deterministik agar tidak salah rute dan tidak berhalusinasi.

---

## 2. Latar Belakang & Masalah

Pencarian informasi internal (data karyawan, lembur, maintenance, SOP, kebijakan, profil
perusahaan) umumnya tersebar di dokumen dan sulit ditanyakan secara natural. Chatbot LLM
murni berisiko **berhalusinasi** karena menjawab dari pengetahuan model, bukan dokumen.

Masalah yang ingin diselesaikan:
- Menjawab pertanyaan **hanya** dari dokumen yang di-ingest (grounded).
- Melayani **dua tipe pertanyaan** sekaligus: lookup terstruktur yang harus *lengkap & eksak*
  (mis. "semua karyawan divisi IT") dan pertanyaan naratif/kebijakan (mis. "bagaimana
  prosedur insiden?").
- Tidak terkunci pada satu dataset; mampu menyerap dokumen baru tanpa mengubah kode.

---

## 3. Tujuan & Sasaran

### 3.1 Tujuan (Goals)
- G1 — Menjawab pertanyaan berbasis dokumen internal dengan **grounding** (anti-halusinasi).
- G2 — Mendukung **lookup terstruktur eksak** (NIK, kode, tanggal, nama, divisi, dst.) yang cepat.
- G3 — Mendukung **pertanyaan naratif/kebijakan** melalui retrieval semantik hybrid.
- G4 — **Memahami bahasa natural/informal** (sinonim, parafrasa) untuk memilih jalur yang tepat.
- G5 — **Tidak bergantung pada satu dataset**: ingestion dokumen generik & skema dataset
  berbasis konfigurasi.
- G6 — **Aman**: memblokir permintaan field sensitif; menolak permintaan di luar dokumen.

### 3.2 Non-Tujuan (Non-Goals) — untuk fase ini
- Autentikasi/otorisasi penuh per-user/role/ACL (saat ini hanya API key tunggal).
- Antarmuka frontend (repositori terpisah).
- Document versioning, delete/reindex pipeline lengkap, background job ingestion.
- RAG evaluation suite otomatis & dashboard observability.
- Multi-tenant / multi-bahasa di luar Indonesia.

---

## 4. Ruang Lingkup

| Termasuk | Tidak termasuk (fase ini) |
|---|---|
| Ingestion PDF/TXT → MinIO + Qdrant + registry Postgres | Frontend, mobile |
| 3-level retrieval (eksak / template / semantik hybrid) | SSO, RBAC, ACL dokumen |
| Query understanding LLM + guard deterministik | Streaming SSE |
| Skema dataset berbasis konfigurasi (DatasetSchema) | Versioning & reindex pipeline |
| API key auth + blokir field sensitif | Background ingestion queue |

---

## 5. Persona & Use Case Utama

- **Karyawan internal** — bertanya data/kebijakan: "Siapa karyawan NIK RU6-1030?",
  "Bagaimana prosedur insiden NusaCloud?"
- **Admin/Operator** — meng-upload & me-reingest dokumen, memantau log.

Use case inti:
1. Upload dokumen → terindeks otomatis.
2. Tanya data terstruktur → jawaban tabel lengkap & eksak (tanpa LLM, ~milidetik).
3. Tanya naratif/kebijakan → jawaban grounded dari chunk relevan (LLM).
4. Pertanyaan sensitif/di luar dokumen → ditolak dengan aman.

---

## 6. Functional Requirements

### FR-1 Ingestion Dokumen
- FR-1.1 Upload PDF/TXT; simpan file asli ke MinIO; catat registry di tabel `documents`.
- FR-1.2 Ekstraksi teks PDF **berbasis koordinat** (rekonstruksi baris) agar heading/section
  terjaga; buang header/footer berulang.
- FR-1.3 **Chunking adaptif**:
  - Dataset terstruktur (skema): 1 record = 1 chunk (`structured_row` / `structured_fact`).
  - Dokumen generik: 1 section/subsection = 1 chunk (`narrative_section`), heading
    (`SECTION N`, `N.N`, `BAB`) jadi `sectionTitle`.
  - Fallback ukuran (`narrative_chunk`) tanpa memotong di tengah kata.
- FR-1.4 Embedding **dense** (Ollama `nomic-embed-text`, 768-dim) + **sparse** (BM25).
- FR-1.5 Upsert ke Qdrant dengan **payload dua-lapis**: field sistem + field dataset
  sesuai recordType (chunk tak membawa slot kosong lintas-tipe).

### FR-2 Skema Dataset Berbasis Konfigurasi (DatasetSchema)
- FR-2.1 Binding chunking, ekstraksi field, dan indeks payload berasal dari konfigurasi
  (`DatasetSchema`), bukan hardcode.
- FR-2.2 Tersedia skema **default** bawaan (dataset contoh) sehingga sistem tetap berjalan
  tanpa konfigurasi eksternal.

### FR-3 Pemahaman Query
- FR-3.1 **Mode Semantic (default):** `QueryUnderstandingService` (LLM, output JSON dipaksa)
  mengekstrak intent + entity.
- FR-3.2 **Fast-path deterministik:** identifier (NIK/kode/tanggal) & nama yang ada di korpus
  dijawab tanpa LLM (~milidetik).
- FR-3.3 **Guard anti-misroute:** strip identifier halusinasi; grounding gate (slot harus ada
  di korpus); intent-sanity (intent tanpa anchor/slot/identifier → dialihkan ke semantik).
- FR-3.4 **Fallback** ke analyzer keyword (`QueryAnalyzerService`) bila LLM gagal parse/timeout.

### FR-4 Retrieval 3 Level
- FR-4.1 **Level 1 — Exact/Structured:** filter payload Qdrant (recordType + field). Tanpa LLM.
- FR-4.2 **Level 2 — Deterministic Template:** jawaban template untuk SOP/audit/profil. Tanpa LLM.
- FR-4.3 **Level 3 — Grounded Semantic/Policy:** hybrid (dense+sparse, RRF) → prompt grounded → LLM.

### FR-5 Generasi Jawaban
- FR-5.1 Jawaban deterministik untuk data terstruktur (formatter).
- FR-5.2 Jawaban LLM **grounded** (hanya dari context; dilarang membuat fakta/izin baru).
- FR-5.3 Bila konteks tak cukup: "Maaf, saya tidak menemukan informasi tersebut."

### FR-6 Keamanan Konten
- FR-6.1 Blokir field sensitif (gaji, rekening, NPWP, password, dll.) di lapisan analisis.
- FR-6.2 API dilindungi header `X-API-Key`.

---

## 7. Arsitektur Sistem

### 7.0 Clean Architecture (3 layer, boundary compiler-enforced)

Kode dipecah menjadi **3 project** (solution `be_service.slnx`); alur runtime tidak berubah,
migrasi hanya memindah file & menegakkan boundary:

- **`be_service.Core`** — Models, `Abstractions/` (8 ports), Services (logika murni & use-case),
  Exceptions. **Tanpa** dependency infra.
- **`be_service.Infrastructure`** — implementasi port: `QdrantService`, `OllamaService`,
  `ObjectStorageService`, `PdfTextExtractor`, `DocumentRepository`, `ChunkRepository`. Referensi Core.
- **`be_service` (Api)** — `Program.cs`, middleware, endpoints. Referensi Core + Infrastructure.

**Dependency rule ditegakkan compiler:** package infra (`Qdrant.Client`, `Npgsql`, `Minio`,
`Pgvector`, `PdfPig`, `iText7`) hanya ada di `be_service.Infrastructure.csproj`, sehingga Core
**tidak bisa** mengimpor Qdrant/Npgsql/Minio (pelanggaran = gagal compile). SQL `documents`
dicabut dari orchestrator ke `DocumentRepository` (Repository pattern). Build dijalankan dari
root solution, bukan single project. Diagram & daftar 8 port: lihat [README.md](../README.md) §2.

### 7.1 Tech Stack
| Komponen | Teknologi |
|---|---|
| API | ASP.NET Core Minimal API (.NET 10), C# |
| Vector store | Qdrant 1.18.1 — named vectors `dense` (768, Cosine) + `sparse` (BM25), fusi RRF |
| LLM & embedding | Ollama — chat `qwen2.5:1.5b`, embedding `nomic-embed-text` |
| Object storage | MinIO (file asli PDF/TXT) |
| Document registry | PostgreSQL/Supabase (`documents`; `document_chunks` opsional) |
| PDF extraction | UglyToad.PdfPig (koordinat) + iText7 |

### 7.2 Komponen Inti
- `RagChatService` — orchestrator chat (pemilihan analyzer, fast-path, guard, dispatch).
- `QueryUnderstandingService` (LLM) / `QueryAnalyzerService` (keyword fallback).
- `StructuredEntityResolver` — nilai entity nyata dari Qdrant (grounding, cache 5 menit).
- `RetrievalService` + `Qdrant*` clients — filter payload & hybrid search.
- `AnswerFormatterService` / `PromptBuilderService` / `OllamaService`.
- Ingestion: `IngestionService` → `DocumentIngestionOrchestrator` → `PdfTextExtractor`,
  `TextNormalizer`, `ChunkingService`, `ChunkMetadataExtractor`, `EmbeddingIngestionService`,
  `QdrantPointWriter`.

### 7.3 Alur Chat (mode Semantic)
```
POST /api/chat
  → fast-path deterministik (NIK/kode/tanggal/nama korpus) ──→ filter payload ──→ jawaban
  → QueryUnderstandingService (LLM, JSON) ─ strip-phantom-id ─ grounding gate ─ intent-sanity
       ├─ AnswerLevel=SemanticGrounded ──→ hybrid (dense+sparse RRF) ──→ PromptBuilder ──→ LLM
       └─ selain itu ──────────────────────→ filter payload / template ──→ jawaban
```

### 7.4 Alur Ingestion
```
Upload → MinIO (file asli) → registry documents
       → ekstraksi teks (koordinat) → normalisasi → chunking (schema/generik)
       → ekstraksi field (schema) → embedding dense+sparse → upsert Qdrant (payload 2-lapis)
```

---

## 8. API Endpoints (aktual)

| Method | Path | Fungsi |
|---|---|---|
| POST | `/api/chat` | Tanya jawab |
| POST | `/api/ingest` | Ingest teks via JSON (debug) |
| POST | `/api/upload-pdf` | Upload PDF |
| POST | `/api/upload-txt` | Upload TXT |
| GET | `/api/qdrant/init` | Buat/pastikan collection |
| POST | `/api/qdrant/recreate` | Drop + buat ulang collection (named vectors) |

Semua endpoint memerlukan header `X-API-Key`. *(Catatan: endpoint health-check belum terpasang.)*

---

## 9. Non-Functional Requirements

| Kode | Kebutuhan | Status |
|---|---|---|
| NFR-1 Performa | Lookup eksak ~milidetik (fast-path); jalur LLM 2–5 dtk (model lokal kecil) | Tercapai untuk eksak; LLM bergantung mesin |
| NFR-2 Grounding | Jawaban hanya dari context; menolak bila tak cukup | Tercapai |
| NFR-3 Keamanan | API key; blokir field sensitif; rahasia via env var | Tercapai (API key tunggal) |
| NFR-4 Reliabilitas | Error dependency → HTTP 503 jelas; fallback analyzer | Tercapai |
| NFR-5 Konfigurabilitas | Skema dataset & parameter via konfigurasi/env var | Tercapai |
| NFR-6 Observability | Logging terstruktur (RETRIEVAL_TRACE, ANSWER_TRACE, QUS_*, dll.) | Logging saja (belum ada dashboard) |

---

## 10. Konfigurasi & Manajemen Rahasia

- Konfigurasi non-rahasia di `appsettings.json` / `appsettings.Example.json` (template tracked).
- **Rahasia via environment variable** (mengoverride appsettings): `Security__ApiKey`,
  `ConnectionStrings__SupabaseDb`, `ObjectStorage__AccessKey`, `ObjectStorage__SecretKey`.
- `appsettings.json` lokal di-`.gitignore` (tak ikut commit).
- Mode RAG: `Rag.Mode` = `Legacy` | `Hybrid` | `Semantic` (aktif: **Semantic**);
  flag `Rag.ShadowCompare` untuk pengukuran (matikan di operasi normal).

---

## 11. Status Implementasi Saat Ini

**Sudah selesai (Tahap 0–3 dari rencana migrasi):**
- ✅ **Migrasi Clean Architecture** — 3 project (Core / Infrastructure / Api), 8 ports,
  boundary dependency **ditegakkan compiler** (package infra terisolasi di Infrastructure).
- ✅ Ingestion generik (ekstraksi koordinat + chunking per-section) & terstruktur.
- ✅ Skema dataset **config-driven** (behavior-preserving, terverifikasi).
- ✅ Query understanding LLM (`format:json` + few-shot sinonim) + fast-path deterministik.
- ✅ Hybrid search dense+sparse (RRF).
- ✅ Guard anti-misroute (phantom-id, grounding gate, intent-sanity, generic-policy→semantik).
- ✅ Manajemen rahasia via env var.

**Mode aktif:** Semantic. Dokumen contoh **dummy Pertamina** (terstruktur) **dan**
dokumen **generik** (NusaCloud/Aurora/Cendana) sama-sama terjawab benar pada pengujian manual.

---

## 12. Keterbatasan Saat Ini (Known Limitations)

1. **Schema-driven belum menyeluruh.** Ingestion/indexing/filtering **sudah** config-driven via
   `DatasetSchema` (default = dataset contoh), dan jalur naratif generik dataset-agnostik —
   namun **structured retrieval cascade** (Level 1) masih **dataset-specific** dan belum
   sepenuhnya digerakkan oleh skema. Dataset terstruktur baru perlu penyesuaian cascade.
2. **Bug routing query SOP** — sebagian pertanyaan SOP/policy bisa salah rute (mis. tertangkap
   jalur eksak/template alih-alih semantik, atau sebaliknya); perlu diperbaiki di planner.
3. **Penanganan input tak koheren** belum tegas — query ambigu/tak bermakna belum selalu
   ditolak/diklarifikasi dengan rapi.
4. **Lapisan jawaban** belum mendukung *field-projection* ("X bekerja di divisi apa" masih
   menampilkan seluruh field); ringkasan LLM kadang kurang presisi.
5. **Retrieval by-equipment** ("teknisi <alat>") belum tersambung ke cascade.
6. **Belum ada** autentikasi per-user/ACL, endpoint health, streaming, document-management/
   delete-reindex, background ingestion, RAG evaluation otomatis.
7. **Tidak ada suite test otomatis** (proyek `Tests/` masih kerangka, di-exclude dari build);
   verifikasi saat ini manual.
8. **Re-ingest menduplikasi** (kemampuan delete-by-document ada tapi belum dipakai) →
   gunakan `POST /api/qdrant/recreate` untuk ingest bersih.
9. **LLM lokal kecil** (1.5B) berisiko parse/latency; dimitigasi `format:json` + retry + fallback.

---

## 13. Roadmap

**Jangka pendek:**
- **Schema-driven retrieval** — jadikan structured cascade (Level 1) digerakkan `DatasetSchema`
  agar dataset terstruktur baru tak perlu ubah kode (ingestion/indexing/filtering sudah schema-driven).
- **Fix bug routing query SOP** di planner (kurangi salah rute SOP/policy).
- **Penanganan input tak koheren** — tolak/klarifikasi query ambigu dengan rapi.
- Field-projection & presisi jawaban (lapisan jawaban).
- Retrieval by-equipment; scope retrieval per-dokumen.
- Endpoint health-check; matikan `ShadowCompare` default; fail-closed API key.

**Jangka menengah:**
- Delete/reindex pipeline + delete-by-documentId saat re-ingest.
- Suite test otomatis + RAG evaluation; observability dashboard.
- Streaming SSE; document-management endpoint.

**Jangka panjang:**
- Autentikasi/ACL per-user/role/department; rate limiting.
- Docker Compose lengkap; deployment hardening; cleanup legacy (Tahap 4).

---

## 14. Pengujian & Kriteria Penerimaan

- Pengujian fungsional manual terdokumentasi di [`MANUAL_TEST_CASES.md`](MANUAL_TEST_CASES.md)
  dan panduan di [`MANUAL_TESTING.md`](MANUAL_TESTING.md).
- Baseline regresi & catatan pengukuran iteratif di [`../BASELINE.md`](../BASELINE.md).
- **Kriteria penerimaan inti:**
  - Query eksak/filter → `source=qdrant_payload`, tanpa LLM, hasil lengkap.
  - Query semantik → `source=qdrant_vector_payload`, grounded.
  - Query negatif/sensitif → tidak berhalusinasi / diblokir; `sources` kosong saat not-found.
  - Tidak ada regresi pada dataset contoh dummy.

---

## 15. Referensi Dokumen Pendukung

| Dokumen | Isi |
|---|---|
| [`README.md`](../README.md) | Setup & menjalankan lokal |
| [`CODEBASE_GUIDE.md`](CODEBASE_GUIDE.md) | Arsitektur teknis & peta kode (onboarding) |
| [`../ANALYSIS.md`](../ANALYSIS.md) | Analisis awal & rencana migrasi (historis) |
| [`../BASELINE.md`](../BASELINE.md) | Baseline regresi & bukti pengukuran |
| [`MANUAL_TEST_CASES.md`](MANUAL_TEST_CASES.md), [`MANUAL_TESTING.md`](MANUAL_TESTING.md) | Test cases & panduan uji manual |

---

## 16. Glosarium

- **RAG** — Retrieval-Augmented Generation: LLM menjawab berbasis dokumen yang di-retrieve.
- **Chunk** — potongan dokumen yang diindeks (1 record atau 1 section).
- **recordType** — tipe data chunk: employee, overtime, maintenance, profile, sop, audit, document.
- **chunkType** — provenance chunk: structured_row, structured_fact, narrative_section, narrative_chunk.
- **Hybrid search (RRF)** — gabungan pencarian dense (embedding) + sparse (BM25) via Reciprocal Rank Fusion.
- **Grounding** — memaksa jawaban hanya dari context dokumen.
- **DatasetSchema** — konfigurasi pemetaan section/field/index per recordType.
