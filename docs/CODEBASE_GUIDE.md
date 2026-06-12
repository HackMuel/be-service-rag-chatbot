# CODEBASE GUIDE — be_service (RAG Backend)

> Dokumen onboarding menyeluruh. **Sumber kebenaran = kode aktual**, bukan README/komentar lama.
> Setiap klaim dirujuk ke `file:line`. Disusun 2026-06-08 dari verifikasi langsung atas kode di
> branch `query-understanding-layer`.

> **PEMBARUAN 2026-06-11 (baca ini dulu — mengoreksi beberapa bagian di bawah):**
> Sejak dokumen ini disusun, beberapa hal berubah. Untuk gambaran produk menyeluruh lihat [PRD.md](PRD.md).
> 1. **`appsettings.json` kini benar-benar di-`.gitignore`** (baris `.gitignore` diperbaiki) + tersedia
>    template tracked [`appsettings.Example.json`](../appsettings.Example.json). Rahasia (API key, dll.)
>    disuntik via **environment variable** (`Security__ApiKey`, `ConnectionStrings__SupabaseDb`, …).
>    → koreksi §0 yang menyebut "untracked, rawan ikut commit".
> 2. **Ingestion config-driven (`DatasetSchema`)** — chunking, ekstraksi field, dan daftar index
>    dibangun dari skema (default = dataset Pertamina) di [Models/DatasetSchemaOptions.cs](../Models/DatasetSchemaOptions.cs),
>    bukan lagi 6 header/belasan method hardcoded. Payload kini **dua-lapis**: field sistem + field
>    dataset per-recordType (chunk sop/profile/document tak lagi membawa slot kosong; ditambah `chunkType`, `ingestedAt`).
> 3. **Guard anti-misroute di planner** ([RagChatService.cs](../Services/RagChatService.cs)):
>    fast-path deterministik (NIK/kode/tanggal/nama-korpus), `StripPhantomIdentifiers`, grounding gate,
>    `ApplyIntentSanityGate`, dan rute generic-policy → semantik. Dokumen generik (SECTION/N.N/BAB)
>    kini terjawab benar.
> 4. **Ekstraksi PDF berbasis koordinat** ([PdfTextExtractor.cs](../Services/Ingestion/PdfTextExtractor.cs))
>    menggantikan flatten-1-baris-per-halaman.
> 5. Endpoint health-check **belum terpasang** (hanya 6 endpoint minimal-API).

---

## §0 — Verifikasi realita (Langkah 0)

| Hal | Kenyataan | Sumber |
|---|---|---|
| Branch aktif | `query-understanding-layer` (jauh di depan `main` — seluruh flow RAG ada di sini) | `git branch` |
| Mode RAG aktif | `Semantic` | [appsettings.json](../appsettings.json) `Rag.Mode` |
| `appsettings.json` | **UNTRACKED** — dilepas dari git (commit `197b7cb`, `52ab280`) lalu di-`.gitignore`. Perubahan di file ini TIDAK ikut commit | `git status` → `?? appsettings.json` |
| Status migrasi | Tahap 0–3 selesai; **Tahap 4 (cleanup legacy) ditunda** | [ANALYSIS.md](../ANALYSIS.md) §8, [BASELINE.md](../BASELINE.md) |

**Status migrasi terhadap rencana [ANALYSIS.md](../ANALYSIS.md) §8:**

- **Tahap 0 (stabilisasi):** ✅ — prompt diperbaiki, `SemanticTopK`/`MaxContextChunks` dinaikkan, `SplitBySize` fallback ([ChunkingService.cs:92](../Services/Ingestion/ChunkingService.cs#L92)).
- **Tahap 1 (pisah dokumen vs structured):** ✅ — dispatch mode di [RagChatService.cs:69](../Services/RagChatService.cs#L69).
- **Tahap 2 (hybrid dense+sparse):** ✅ — named vectors `dense`+`sparse` ([QdrantCollectionService.cs:30-41](../Services/Qdrant/QdrantCollectionService.cs#L30-L41)), BM25 ([SparseBm25Encoder.cs](../Services/SparseBm25Encoder.cs)), RRF fusion ([QdrantSearchClient.cs:124](../Services/Qdrant/QdrantSearchClient.cs#L124)).
- **Tahap 3 (query understanding LLM):** ✅ — [QueryUnderstandingService.cs](../Services/QueryUnderstandingService.cs), default `Semantic`.
- **Tahap 4 (hapus legacy):** ⏳ DITUNDA — `QueryAnalyzerService`, template `AnswerFormatterService`, `TextNormalizer` dummy masih dipertahankan sebagai fallback.

---

## §1 — Peta file per fungsi

Tag: **[AKTIF]** dipakai di jalur utama · **[LEGACY—fallback]** hanya saat fallback, target hapus Tahap 4 · **[DUMMY-SPECIFIC]** logika terkunci ke dataset contoh Pertamina.

### Entry & infrastruktur
| File | Tag | Peran |
|---|---|---|
| [Program.cs](../Program.cs) | AKTIF | DI container + 6 endpoint minimal API |
| [Services/ApiKeyMiddleware.cs](../Services/ApiKeyMiddleware.cs) | AKTIF | Header `X-API-Key` |
| [Services/GlobalExceptionHandlingMiddleware.cs](../Services/GlobalExceptionHandlingMiddleware.cs) | AKTIF | Map exception → HTTP (mis. `SERVICE_UNAVAILABLE`) |
| [Services/HttpResponseGuard.cs](../Services/HttpResponseGuard.cs) | AKTIF | Wrapper HTTP ke Qdrant/Ollama + error |

### Query understanding
| File | Tag | Peran |
|---|---|---|
| [Services/QueryUnderstandingService.cs](../Services/QueryUnderstandingService.cs) | **AKTIF** | Analyzer default (LLM, `format:"json"`) |
| [Services/QueryAnalyzerService.cs](../Services/QueryAnalyzerService.cs) | **LEGACY—fallback** | Keyword cascade; dipanggil hanya saat QUS gagal |
| [Services/FieldIntentClassifier.cs](../Services/FieldIntentClassifier.cs) | LEGACY—fallback | LLM lazy untuk field sensitif; hanya via jalur legacy |
| [Services/FieldSchema.cs](../Services/FieldSchema.cs) | AKTIF | Blocklist field sensitif + `FieldKeywordMap` |
| [Models/RagQueryAnalysis.cs](../Models/RagQueryAnalysis.cs) · [Models/AnswerLevel.cs](../Models/AnswerLevel.cs) · [Models/RagModeOptions.cs](../Models/RagModeOptions.cs) | AKTIF | Kontrak output + level jawaban + mode |

### Orkestrasi & retrieval
| File | Tag | Peran |
|---|---|---|
| [Services/RagChatService.cs](../Services/RagChatService.cs) | AKTIF | Orkestrator `/api/chat` |
| [Services/RetrievalService.cs](../Services/RetrievalService.cs) | AKTIF | Cascade routing 16+ cabang + semantic |
| [Services/StructuredEntityResolver.cs](../Services/StructuredEntityResolver.cs) | AKTIF | Fuzzy-match entity dari nilai NYATA di Qdrant (dataset-agnostik) |
| [Services/SparseBm25Encoder.cs](../Services/SparseBm25Encoder.cs) | AKTIF | Sparse BM25 TF |
| [Services/RetrievalModes.cs](../Services/RetrievalModes.cs) · [Models/RagRetrievalResult.cs](../Models/RagRetrievalResult.cs) · [Models/RetrievalOptions.cs](../Models/RetrievalOptions.cs) | AKTIF | Konstanta + hasil + tuning |

### Answer building
| File | Tag | Peran |
|---|---|---|
| [Services/AnswerFormatterService.cs](../Services/AnswerFormatterService.cs) | **AKTIF tapi DUMMY-SPECIFIC** | Template deterministik per record-type |
| [Services/PromptBuilderService.cs](../Services/PromptBuilderService.cs) | AKTIF | 3 varian prompt LLM |
| [Services/OllamaService.cs](../Services/OllamaService.cs) | AKTIF | Client LLM + embedding (HTTP) |
| [Services/ChunkMetadataExtractor.cs](../Services/ChunkMetadataExtractor.cs) | DUMMY-SPECIFIC | Regex ekstraksi field dari teks chunk |

### Ingestion
| File | Tag | Peran |
|---|---|---|
| [Services/IngestionService.cs](../Services/IngestionService.cs) | AKTIF | Upload PDF/TXT → MinIO → orchestrator |
| [Services/Ingestion/PdfTextExtractor.cs](../Services/Ingestion/PdfTextExtractor.cs) | AKTIF | PDF → teks (PdfPig) |
| [Services/Ingestion/TextNormalizer.cs](../Services/Ingestion/TextNormalizer.cs) | **DUMMY-SPECIFIC** | Regex normalisasi terkunci ke 6 section dummy |
| [Services/Ingestion/ChunkingService.cs](../Services/Ingestion/ChunkingService.cs) | AKTIF (+`SplitBySize` generik) | Section-split dummy + fallback ukuran |
| [Services/Ingestion/EmbeddingIngestionService.cs](../Services/Ingestion/EmbeddingIngestionService.cs) | AKTIF | Dense (Ollama) + sparse (BM25) |
| [Services/Ingestion/DocumentIngestionOrchestrator.cs](../Services/Ingestion/DocumentIngestionOrchestrator.cs) | AKTIF | Pipeline ingest end-to-end |

### Qdrant & storage
| File | Tag | Peran |
|---|---|---|
| [Services/Qdrant/QdrantCollectionService.cs](../Services/Qdrant/QdrantCollectionService.cs) | AKTIF | Buat collection named vectors + payload index |
| [Services/Qdrant/QdrantPointWriter.cs](../Services/Qdrant/QdrantPointWriter.cs) | AKTIF | Upsert (dense+sparse+payload); `DeleteByDocumentId` (tak dipakai) |
| [Services/Qdrant/QdrantSearchClient.cs](../Services/Qdrant/QdrantSearchClient.cs) | AKTIF | Dense/Hybrid RRF + filter payload + per-field search |
| [Services/Qdrant/QdrantScrollClient.cs](../Services/Qdrant/QdrantScrollClient.cs) | AKTIF | Scroll filter + `GetKnownStructuredEntities` |
| [Services/Qdrant/QdrantFilterBuilder.cs](../Services/Qdrant/QdrantFilterBuilder.cs) | AKTIF | Bangun filter payload |
| [Services/QdrantService.cs](../Services/QdrantService.cs) | AKTIF | Facade ke 5 client di atas |
| [Services/ObjectStorageService.cs](../Services/ObjectStorageService.cs) | AKTIF | Upload dokumen asli ke MinIO |
| [Repositories/ChunkRepository.cs](../Repositories/ChunkRepository.cs) | AKTIF (opsional) | Simpan chunk ke Postgres bila `StorageMode` aktif |

---

## §2 — "Kalau backend RAG ini dibangun dari 0"

Urutan konseptual membangun, dipetakan ke implementasi nyata di repo:

1. **Siapkan vector store + skema.** Collection Qdrant `pertamina_chunks` dengan **named vectors**:
   `dense` (768-dim, Cosine) + `sparse` (BM25 TF) → [QdrantCollectionService.cs:30-41](../Services/Qdrant/QdrantCollectionService.cs#L30-L41).
   Plus 17 payload index `keyword` untuk filter exact → [QdrantCollectionService.cs:88-109](../Services/Qdrant/QdrantCollectionService.cs#L88-L109).
2. **Ingestion: dokumen → chunk.** Ekstrak teks ([PdfTextExtractor.cs](../Services/Ingestion/PdfTextExtractor.cs)) → normalisasi ([TextNormalizer.cs:7](../Services/Ingestion/TextNormalizer.cs#L7)) → chunking per-record ([ChunkingService.cs:21](../Services/Ingestion/ChunkingService.cs#L21)).
3. **Embedding ganda.** Dense via Ollama `nomic-embed-text` ([EmbeddingIngestionService.cs:19](../Services/Ingestion/EmbeddingIngestionService.cs#L19)) + sparse BM25 ([EmbeddingIngestionService.cs:36-39](../Services/Ingestion/EmbeddingIngestionService.cs#L36-L39)).
4. **Tulis ke store.** Upsert point: vektor `dense`+`sparse` + 22 field payload → [QdrantPointWriter.cs:63](../Services/Qdrant/QdrantPointWriter.cs#L63), payload di [QdrantPointWriter.cs:151](../Services/Qdrant/QdrantPointWriter.cs#L151).
5. **Retrieval.** Dua strategi: **filter payload** (exact/structured) dan **hybrid RRF** (semantic) → [RetrievalService.cs:33](../Services/RetrievalService.cs#L33) & [QdrantSearchClient.cs:124](../Services/Qdrant/QdrantSearchClient.cs#L124).
6. **Query understanding.** Pahami intent + entity dari pertanyaan → [QueryUnderstandingService.cs:61](../Services/QueryUnderstandingService.cs#L61).
7. **Answer building.** Template deterministik ([AnswerFormatterService.cs:8](../Services/AnswerFormatterService.cs#L8)) atau LLM grounded ([PromptBuilderService.cs](../Services/PromptBuilderService.cs) → [OllamaService.cs:68](../Services/OllamaService.cs#L68)).

> **KENAPA dua strategi retrieval?** Data tabular (40 karyawan, 30 lembur, 24 maintenance)
> butuh jawaban **lengkap & eksak** → filter payload. Pertanyaan natural butuh **relevansi semantik**
> → hybrid. Vektor saja akan mengembalikan top-K (tidak lengkap) untuk "list semua".

---

## §3 — Flow INGESTION per tahap

Entry: [IngestionService.cs](../Services/IngestionService.cs) (`IngestPdfAsync`/`IngestTxtAsync`/`IngestAsync`) → [DocumentIngestionOrchestrator.IngestAsync](../Services/Ingestion/DocumentIngestionOrchestrator.cs#L42).

| # | Tahap | Input → Output | File:line | Dummy? |
|---|---|---|---|---|
| 1 | Upload + simpan asli | `IFormFile` → objek di MinIO + `objectKey` | [IngestionService.cs:21-47](../Services/IngestionService.cs#L21-L47), [ObjectStorageService.cs](../Services/ObjectStorageService.cs) | tidak |
| 2 | Ekstrak teks (PDF) | PDF → string per `[PAGE:N]` | [PdfTextExtractor.cs](../Services/Ingestion/PdfTextExtractor.cs) | tidak |
| 3 | Registry dokumen | insert ke tabel Postgres `documents` → `documentId` | [DocumentIngestionOrchestrator.cs:53-65](../Services/Ingestion/DocumentIngestionOrchestrator.cs#L53-L65) | tidak |
| 4 | Normalisasi | teks → teks rapi | [DocumentIngestionOrchestrator.cs:69](../Services/Ingestion/DocumentIngestionOrchestrator.cs#L69) → [TextNormalizer.cs:7](../Services/Ingestion/TextNormalizer.cs#L7) | **YA** — regex `NormalizeEmployeeTable/Overtime/Maintenance` ([:103-165](../Services/Ingestion/TextNormalizer.cs#L103)) |
| 5 | Chunking | teks → `List<chunk>` (1 record = 1 chunk) | [ChunkingService.cs:21](../Services/Ingestion/ChunkingService.cs#L21) | **YA** untuk 6 header section ([:23](../Services/Ingestion/ChunkingService.cs#L23)); `SplitBySize` ([:92](../Services/Ingestion/ChunkingService.cs#L92)) generik |
| 6 | Embedding | chunk → dense `List<float>` + sparse `Dict<uint,float>` | [DocumentIngestionOrchestrator.cs:102-106](../Services/Ingestion/DocumentIngestionOrchestrator.cs#L102-L106) | tidak (sparse); ekstraksi metadata regex **YA** |
| 7 | Ekstrak metadata payload | chunk → 22 field (NIK, divisi, dll.) | [DocumentIngestionOrchestrator.cs:151-185](../Services/Ingestion/DocumentIngestionOrchestrator.cs#L151-L185) via [ChunkMetadataExtractor](../Services/ChunkMetadataExtractor.cs) | **YA** (regex pola dummy) |
| 8 | (Opsional) chunk ke Postgres | bila `StorageMode.WriteDocumentChunksToPostgres=true` | [DocumentIngestionOrchestrator.cs:97-100](../Services/Ingestion/DocumentIngestionOrchestrator.cs#L97-L100) | tidak |
| 9 | Upsert Qdrant | chunk+vektor → point | [DocumentIngestionOrchestrator.cs:108](../Services/Ingestion/DocumentIngestionOrchestrator.cs#L108) → [QdrantPointWriter.cs:63](../Services/Qdrant/QdrantPointWriter.cs#L63) | tidak |

> ⚠️ **Setiap ingest membuat `documentId` baru** ([:88](../Services/Ingestion/DocumentIngestionOrchestrator.cs#L88)) dan **tidak** memanggil `DeleteByDocumentIdAsync` → re-ingest dokumen sama = duplikat. Lihat §8.

---

## §4 — Flow CHAT per tahap (mode Semantic)

Entry: [Program.cs:78](../Program.cs#L78) `POST /api/chat` → [RagChatService.AskAsync:37](../Services/RagChatService.cs#L37).

```
AskAsync
 ├─ 1. Pilih analyzer            [RagChatService.cs:46]
 │     Mode=Semantic → QueryUnderstandingService.AnalyzeAsync (LLM)
 │     (else)        → QueryAnalyzerService.AnalyzeAsync (keyword)
 ├─ 2. Log fallback + shadow     [RagChatService.cs:49-51]  (shadow hanya bila ShadowCompare=true)
 ├─ 3. Blocked?                  [RagChatService.cs:60]     → tolak field sensitif
 ├─ 4. SemanticGrounded?         [RagChatService.cs:69]     → AskSemanticAsync (vektor hybrid murni)
 └─ 5. else                      [RagChatService.cs:72]     → AskLegacyAsync → RetrieveAsync
```

**Tahap detail:**

| # | Tahap | File:line | Catatan |
|---|---|---|---|
| 1 | Query understanding (LLM) | [QueryUnderstandingService.cs:61](../Services/QueryUnderstandingService.cs#L61) | Panggil Ollama dgn `format:"json"`+`temperature:0` ([:73](../Services/QueryUnderstandingService.cs#L73)); output → `RagQueryAnalysis` |
| 1b | **Retry parse** | [QueryUnderstandingService.cs:79-80](../Services/QueryUnderstandingService.cs#L79-L80) | 1× retry bila JSON tak terparse |
| 1c | **FALLBACK ke legacy** | [QueryUnderstandingService.cs:89](../Services/QueryUnderstandingService.cs#L89) (`reason=parse`), [:107](../Services/QueryUnderstandingService.cs#L107) (`reason=error`) | KAPAN: JSON tetap gagal setelah retry, ATAU panggilan LLM exception/timeout |
| 2 | Penentuan AnswerLevel | [QueryUnderstandingService.cs:263](../Services/QueryUnderstandingService.cs#L263) | `ExactStructured` / `DeterministicTemplate` / `PolicyGrounded` / `SemanticGrounded` |
| 3 | Keputusan routing utama | [RagChatService.cs:69](../Services/RagChatService.cs#L69) | `UsesSemanticDispatch && SemanticGrounded` → jalur vektor; else → legacy cascade |
| 4a | Jalur SEMANTIC | [RagChatService.cs:156](../Services/RagChatService.cs#L156) → [RetrievalService.cs:328](../Services/RetrievalService.cs#L328) | Hybrid RRF murni, top-K difilter threshold |
| 4b | Jalur STRUCTURED | [RagChatService.cs:181](../Services/RagChatService.cs#L181) → [RetrievalService.cs:33](../Services/RetrievalService.cs#L33) | Cascade if/else → filter payload |
| 4b-i | Resolver fallback | [RetrievalService.cs:251-264](../Services/RetrievalService.cs#L251-L264) | KAPAN: cascade kosong → fuzzy-match entity dari Qdrant |
| 4b-ii | Generic record-type | [RetrievalService.cs:266-274](../Services/RetrievalService.cs#L266-L274) | KAPAN: masih kosong + `GenericRecordType` ada → scroll "list semua" |
| 4b-iii | Vector last-resort | [RetrievalService.cs:276](../Services/RetrievalService.cs#L276) | KAPAN: semua di atas kosong & bukan `ExactStructured` |
| 5 | Answer: template dulu | [RagChatService.cs:246](../Services/RagChatService.cs#L246) → [AnswerFormatterService.cs:8](../Services/AnswerFormatterService.cs#L8) | Bila kosong → LLM |
| 6 | Answer: LLM | [RagChatService.cs:266](../Services/RagChatService.cs#L266) | `BuildPolicyGroundedPrompt` (Policy) atau `Build` (lain) → [OllamaService.cs:68](../Services/OllamaService.cs#L68) |
| 7 | Guard "tidak ditemukan" | [RagChatService.cs:311](../Services/RagChatService.cs#L311) | Deteksi frasa not-found |

> **Routing utama diambil di [RetrievalService.cs:68-249](../Services/RetrievalService.cs#L68-L249)** — urutan: NIK → kode MT → tanggal → audit → sop → profile → employee(divisi/shift/status/posisi) → overtime(approval/divisi) → maintenance(status/lokasi/teknisi) → exact-name. Urutan ini **menentukan** (cabang pertama yang cocok menang).

---

## §5 — Konfigurasi & dependensi yang menentukan perilaku

### `RagModeOptions` → [Models/RagModeOptions.cs](../Models/RagModeOptions.cs)
| Properti | Efek |
|---|---|
| `Mode` (=`Semantic`) | `IsSemanticQueryMode` → pakai QUS; else keyword analyzer |
| `ShadowCompare` (=`false`) | Bila true, tiap query juga jalankan legacy analyzer + log `QUS_VS_LEGACY` (overhead pengukuran) |
| `UsesSemanticDispatch` (computed) | `Hybrid \|\| Semantic` → SemanticGrounded ke jalur vektor |

### `appsettings.json` (UNTRACKED) — key penting
- **Rag:** `Mode=Semantic`, `ShadowCompare=false`
- **Ollama:** `ChatModel=qwen2.5:1.5b`, `EmbeddingModel=nomic-embed-text`, `TimeoutSeconds=120` ([Models/OllamaOptions.cs](../Models/OllamaOptions.cs))
- **Qdrant:** `CollectionName=pertamina_chunks`, `VectorSize=768`, `Distance=Cosine`, `GrpcPort=6334` ([Models/QdrantOptions.cs](../Models/QdrantOptions.cs))
- **Retrieval:** `SemanticTopK=15`, `SemanticScoreThreshold=0.55`, `SemanticMaxContextChunks=5`, `StructuredDefaultLimit=50`, **`HybridSearchEnabled=true`** ([Models/RetrievalOptions.cs](../Models/RetrievalOptions.cs)). Catatan: default kode `HybridSearchEnabled=false` ([:16](../Models/RetrievalOptions.cs#L16)) — wajib di-true di appsettings agar sparse aktif.
- **Security:** `ApiKey` (header `X-API-Key`; pernah dirotasi saat sesi pengembangan)

### Dependensi eksternal (semua wajib hidup)
- **Ollama** `:11434` — chat + embedding. Mati → chat gagal di tahap understanding/answer.
- **Qdrant** `:6333` (HTTP) / `:6334` (gRPC) — vector store. **Mati → SEMUA query gagal** di retrieval (`SERVICE_UNAVAILABLE`).
- **MinIO** `:9000` — simpan dokumen asli saat upload.
- **Postgres** (Supabase `:54322`) — registry `documents` (selalu) + chunk opsional (`StorageMode`).

---

## §6 — Perubahan terbaru yang BELUM terdokumentasi & BELUM dikunci

Dua perubahan dari ronde robustness terakhir (lihat [BASELINE.md](../BASELINE.md) bagian "Robustness #1/#2"):

1. **`OllamaService.CompleteAsync` parameter `format`** → [OllamaService.cs:102](../Services/OllamaService.cs#L102), set `request["format"]` di [:125](../Services/OllamaService.cs#L125). QUS memanggil dengan `format:"json"` ([QueryUnderstandingService.cs:73](../Services/QueryUnderstandingService.cs#L73)) → Ollama dijamin keluarkan JSON valid.
2. **Sinonim + few-shot di prompt QUS** → mandor→Supervisor ([:29](../Services/QueryUnderstandingService.cs#L29),[:38](../Services/QueryUnderstandingService.cs#L38)), contoh profile & equipment.

**Efek terukur:** parse-fail **3/17 → 0/17** pada BASELINE; query gagal "ada mandor", "jelaskan perusahaan" kini benar; tanpa regresi B/D/E.

> ⚠️ **BELUM DIKUNCI:** Hasil ini **belum** ditulis sebagai kolom resmi di tabel BASELINE.md
> dan **belum di-commit**. Status git: `M Services/OllamaService.cs`, `M Services/QueryUnderstandingService.cs`,
> `M BASELINE.md` masih working-tree. Perubahan `appsettings.json` (Mode/ShadowCompare) ada di file untracked → tidak akan ter-commit.

---

## §7 — Rencana migrasi selanjutnya

Diturunkan dari kondisi kode + [ANALYSIS.md](../ANALYSIS.md) §7-8 + [BASELINE.md](../BASELINE.md).

### Loose end (kunci dulu sebelum lanjut)
1. **Kunci hasil robustness di BASELINE.md** — tambahkan kolom aktual + commit terpisah `feat/qus-json-robustness`.
2. **Jalankan set lengkap + negative test** — termasuk query identifier yang TIDAK ada (RU6-0001, MT-001) untuk pastikan "not found" yang benar.
3. **Commit terpisah & rapi** — pisahkan: (a) semantic-mode+shadow flag, (b) fix all-records routing, (c) robustness JSON+prompt.

### Backlog (urut prioritas dampak)
- **Streaming SSE** — saat ini `stream=false` ([OllamaService.cs:68](../Services/OllamaService.cs#L68)); UX response panjang buruk.
- **Document management endpoint** — list/hapus dokumen; manfaatkan `DeleteByDocumentIdAsync` yang sudah ada tapi nganggur.
- **Tahap 4 cleanup** — hapus `QueryAnalyzerService`/template `AnswerFormatterService`/`TextNormalizer`; **tunggu dataset asli + Semantic terbukti stabil di traffic nyata**.
- **Adaptasi dataset asli** — ganti regex dummy ([TextNormalizer.cs](../Services/Ingestion/TextNormalizer.cs), [ChunkMetadataExtractor.cs](../Services/ChunkMetadataExtractor.cs)) ke chunking/ekstraksi generik.
- **Admin dashboard** — observabilitas (fallback rate, retrieval mode, latensi).

### Kandidat retrieval/answer yang ditunda (dari probe BASELINE)
- **Equipment-retrieval** — "siapa teknisi generator": intent+equipment sudah benar, tapi cascade belum punya cabang by-equipment. Method `SearchMaintenanceByEquipmentAsync` **sudah ada** ([QdrantSearchClient.cs:343](../Services/Qdrant/QdrantSearchClient.cs#L343)) — tinggal di-wire di [RetrievalService.cs:192-220](../Services/RetrievalService.cs#L192-L220).
- **Policy P9** — "boleh ga orang IT masuk tangki": analisis benar (PolicyGrounded) tapi jawaban ketimpa template SOP generik ([AnswerFormatterService.cs](../Services/AnswerFormatterService.cs)). Perbaikan di lapisan jawaban.
- **Field-projection C1** — "divisi sinta" → dump semua field, bukan hanya divisi. Lapisan jawaban; tak ada konsep "field diminta" selain blokir sensitif.

---

## §8 — TEMUAN (kontradiksi kode vs dokumentasi/komentar) — TIDAK diperbaiki

1. **`SearchSemanticOnlyAsync` komentar usang.** Komentar bilang "Used by the semantic dispatch path when RagMode = Hybrid" ([RetrievalService.cs:327](../Services/RetrievalService.cs#L327)), padahal kini juga dipakai mode **Semantic** via `UsesSemanticDispatch` ([RagChatService.cs:69](../Services/RagChatService.cs#L69)).

2. **`DeleteByDocumentIdAsync` ada tapi tak terpakai.** Kemampuan dedup re-ingest sudah diimplementasi ([QdrantPointWriter.cs:101](../Services/Qdrant/QdrantPointWriter.cs#L101)) tapi orchestrator tak pernah memanggilnya → re-ingest tetap duplikat (kontradiksi dengan rencana S-6 di [ANALYSIS.md](../ANALYSIS.md) §7).

3. **`SearchMaintenanceByEquipmentAsync` ada tapi tak ter-wire.** Method search by-equipment lengkap ([QdrantSearchClient.cs:343](../Services/Qdrant/QdrantSearchClient.cs#L343)) tapi tak ada cabang di cascade `RetrievalService` yang memanggilnya.

4. **Klaim BASELINE "T0 fixed C1: hanya divisi" menyesatkan.** QW-1 hanya mengubah **prompt LLM** ([PromptBuilderService.cs:37](../Services/PromptBuilderService.cs#L37)), sedangkan query nama (`exact-name`) memakai **deterministic answer** ([AnswerFormatterService.cs:8](../Services/AnswerFormatterService.cs#L8)) yang tak melewati prompt itu → C1 sebenarnya belum diperbaiki untuk jalur ini.

5. **Default `HybridSearchEnabled` berbeda kode vs config.** Default kode `false` ([RetrievalOptions.cs:16](../Models/RetrievalOptions.cs#L16)) tapi appsettings `true`. Bila appsettings hilang/ter-reset, sparse diam-diam mati dan retrieval turun ke dense-only tanpa error.

6. **`Fuzzy*Threshold` & magic number.** Threshold fuzzy resolver ([RetrievalOptions.cs:19-20](../Models/RetrievalOptions.cs#L19-L20)) sudah diekspos (QW-5 selesai), tapi priority score di [StructuredEntityResolver.cs](../Services/StructuredEntityResolver.cs) masih hardcoded.

7. **Komentar ANALYSIS.md menyebut "Qdrant diakses via raw HttpClient".** Sebagian benar: collection/upsert/scroll pakai raw HTTP, tapi **search dense/hybrid kini pakai Qdrant.Client SDK** ([QdrantSearchClient.cs:63](../Services/Qdrant/QdrantSearchClient.cs#L63),[:149](../Services/Qdrant/QdrantSearchClient.cs#L149)) — campuran, bukan murni raw HTTP.

---

*Akhir dokumen. Catatan: file ini read-only terhadap kode — tidak ada logika aplikasi yang diubah dalam pembuatannya.*
