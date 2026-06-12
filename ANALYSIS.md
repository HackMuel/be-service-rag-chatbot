> **STATUS DOKUMEN (2026-06-11): HISTORIS / SNAPSHOT AWAL.**
> Dokumen ini adalah analisis arsitektur & rencana migrasi **sebelum** refactor dikerjakan.
> Bagian "Kondisi Sekarang" di bawah memotret keadaan LAMA (banyak hardcode) — sebagian besar
> **sudah berubah**. Rencana migrasi **Tahap 0–3 sudah selesai** (query understanding LLM,
> hybrid dense+sparse, chunking generik, DatasetSchema config-driven, guard anti-misroute);
> **Tahap 4 (cleanup legacy) ditunda**. Untuk kondisi terkini lihat [docs/PRD.md](docs/PRD.md)
> dan [docs/CODEBASE_GUIDE.md](docs/CODEBASE_GUIDE.md). Dokumen ini dipertahankan sebagai
> rekam jejak rasional desain.

---

1. Ringkasan Arsitektur
Struktur folder:


be_service/
├── Program.cs                          ← Entry point, Minimal API
├── Models/                             ← DTO, options, enums
├── Services/
│   ├── RagChatService.cs               ← Orchestrator utama
│   ├── QueryAnalyzerService.cs         ← Query parsing (≈890 baris)
│   ├── RetrievalService.cs             ← Routing retrieval (≈681 baris)
│   ├── AnswerFormatterService.cs       ← Template answer builder
│   ├── PromptBuilderService.cs         ← LLM prompt assembly
│   ├── StructuredEntityResolver.cs     ← Fuzzy entity matching
│   ├── OllamaService.cs                ← LLM + embedding client (HTTP)
│   ├── FieldIntentClassifier.cs        ← LLM-based field blocker
│   ├── FieldSchema.cs                  ← Blocklist sensitive fields
│   ├── QueryHelpers.cs                 ← Shared utilities
│   ├── RetrievalModes.cs               ← String constants
│   ├── Ingestion/
│   │   ├── ChunkingService.cs
│   │   ├── TextNormalizer.cs
│   │   ├── PdfTextExtractor.cs
│   │   ├── EmbeddingIngestionService.cs
│   │   └── DocumentIngestionOrchestrator.cs
│   └── Qdrant/
│       ├── QdrantCollectionService.cs
│       ├── QdrantPointWriter.cs
│       ├── QdrantSearchClient.cs
│       ├── QdrantScrollClient.cs
│       └── QdrantFilterBuilder.cs
└── Repositories/
    └── ChunkRepository.cs              ← PostgreSQL (opsional, StorageMode)
Diagram alur request → response:


POST /api/chat { message }
        │
   RagChatService.AskAsync               [RagChatService.cs:30]
        │
   QueryAnalyzerService.AnalyzeAsync     [QueryAnalyzerService.cs:38]
        │  30+ keyword array checks + 15+ regex + FieldIntentClassifier (lazy LLM)
        ▼
   RagQueryAnalysis (24 field struct)
        │
   RetrievalService.RetrieveAsync        [RetrievalService.cs:30]
        │  if/else cascade → 16 path berbeda → Qdrant
        │  Fallback terakhir: vector search (SearchSemanticAsync)
        ▼
   List<RetrievedChunk>
        │
   AnswerFormatterService                [RagChatService.cs:102]
        │  TryBuildDeterministicAnswer → jika kosong → ke LLM
        ▼
   PromptBuilderService.Build            [RagChatService.cs:121-123]
        │
   OllamaService.GenerateChatAsync      [OllamaService.cs:68]
        │  HTTP POST ke Ollama /api/generate, stream=false
        ▼
   ChatResponse { Answer, Sources }
NuGet dependencies relevan:

Package	Versi	Fungsi
Qdrant.Client	1.18.1	Qdrant SDK (ada, tapi tidak dipakai — Qdrant diakses via raw HttpClient)
UglyToad.PdfPig	1.7.0-custom-5	PDF parsing
itext7	9.6.0	PDF (diimpor, tapi PdfTextExtractor pakai PdfPig)
Npgsql	10.0.2	PostgreSQL (untuk optional chunk storage)
Pgvector	0.3.2	Vector type Postgres (diimpor, tidak aktif)
Minio	7.0.0	Object storage untuk dokumen asli
Newtonsoft.Json	13.0.4	JSON serialization
Catatan: Tidak ada Ollama SDK — LLM dan embedding diakses via HttpClient manual.

2. Pipeline Ingestion
Load:

TXT: StreamReader langsung (IngestionService.cs:62-67)
PDF: UglyToad.PdfPig → page.GetWords() per halaman, join dengan spasi → join halaman dengan \n\n[PAGE:N]\n (PdfTextExtractor.cs:9-14)
Cleaning (TextNormalizer.cs):

Normalisasi line ending
Regex inject \n\n sebelum \d+\. [A-Z] (TextNormalizer.cs:14-17)
Fix spasi RU 6- → RU6- (TextNormalizer.cs:19-22)
Fix spasi HSSEAnalyst → HSSE Analyst (TextNormalizer.cs:24-27)
Fix A Tetap → A Tetap (TextNormalizer.cs:29-32)
NormalizeProfileSection, NormalizeEmployeeTable, NormalizeOvertimeTable, NormalizeMaintenanceTable — semua berbasis regex hardcoded ke dummy dataset
Chunking (ChunkingService.cs):

Strategy: structure-aware, berbasis 6 section header tetap (ChunkingService.cs:9):

"1. Profil Perusahaan" | "2. Data Karyawan Internal" | "3. SOP Keamanan Area Kilang" |
"4. Rekap Lembur Karyawan" | "5. Log Maintenance Peralatan" | "6. Catatan Audit dan Keamanan"
Per section: split per record (NIK untuk employee, tanggal untuk overtime, kode MT untuk maintenance)
Tidak ada ukuran chunk, tidak ada overlap — setiap record = 1 chunk
Fallback generic: tidak ada — dokumen yang tidak cocok dengan pattern akan menjadi 1 chunk besar
Embedding:

Model: nomic-embed-text via Ollama (appsettings.json:8)
Dimensi: 768 (appsettings.json:7, dikonfirmasi di QdrantOptions)
Request: POST /api/embed dengan { model, input } (OllamaService.cs:31-65)
Metadata yang disimpan per chunk (17 field di Qdrant payload):
documentId, documentTitle, content, recordType, nik, name, nameNormalized, maintenanceCode, date, division, department, position, shift, employeeStatus, duration, approval, equipment, location, maintenanceStatus, technician, sectionTitle, chunkIndex, pageNumber

3. Vector Store (Qdrant)
Collection config (QdrantCollectionService.cs):

Single dense vector (bukan named vectors, bukan sparse)
Distance: Cosine (appsettings.json:9)
Size: 768 (appsettings.json:7)
Tidak ada HNSW tuning eksplisit
Payload index yang dibuat (field_schema = "keyword" semua):
recordType, nik, nameNormalized, maintenanceCode, date, division, department, position, shift, employeeStatus, duration, approval, equipment, location, maintenanceStatus, technician, sectionTitle

Update/delete: UpsertChunkAsync menggunakan PUT ke /collections/{name}/points — idempotent per point ID (Guid dibuat baru setiap ingest). Tidak ada mekanisme delete by documentId, sehingga re-ingest dokumen yang sama menimbulkan duplikasi — data lama tidak dihapus.

4. Retrieval — Inventory Hardcoded Logic
TINGGI — Query classifier (sepenuhnya keyword-based)
File:Line	Fungsi	Keyword Hardcoded
QueryAnalyzerService.cs:195-216	IsSopQuery	17 keyword: "sop", "apd", "tangki", "safety briefing", "shift malam", …
QueryAnalyzerService.cs:218-233	IsProfileQuery	11 keyword: "profil", "kapasitas produksi", "direktur operasi", …
QueryAnalyzerService.cs:235-248	IsAuditQuery	9 keyword: "audit", "logbook", "anomali suhu", "kepatuhan", …
QueryAnalyzerService.cs:254-272	IsOvertimeQuery, IsMaintenanceQuery	"lembur", "rekap lembur", "maintenance", "teknisi", …
QueryAnalyzerService.cs:274-311	IsAccessQuestion, IsPermissionQuestion, IsPolicyQuestion	"boleh", "izin", "akses", "non-hsse", "divisi lain", …
QueryAnalyzerService.cs:428-439	IsSemanticGroundedQuestion	"ringkas", "jelaskan", "bagaimana", "risiko", "prosedur"
Konsekuensi: Query "apakah peraturan APD berlaku di malam hari?" → IsSopQuery=true (karena "apd") DAN IsDeterministicTemplateQuestion=true (karena "apd wajib" tidak ada, tapi IsProfileQuery juga false) → bisa salah path. Query yang menggunakan sinonim atau bahasa informal tidak tertangkap.

TINGGI — Entity extraction hardcoded ke data dummy
File:Line	Fungsi	Nilai Hardcoded
QueryAnalyzerService.cs:447-475	ExtractDivisionFromQuestion	8 nama divisi: "IT & Digitalisasi", "Operasional Kilang", "Human Capital", "HSSE", … Bahkan \bIT\b regex khusus
QueryAnalyzerService.cs:501-513	ExtractPositionFromQuestion	Array jabatan: ["Supervisor", "Operator", "Manager", "Coordinator", "Analyst", "Engineer", "Staff"]
QueryAnalyzerService.cs:545-555	ExtractLocationFromQuestion	Array lokasi: ["Gate Utama", "Utility Plant", "Area Produksi B", "Area Tanki A", "Warehouse"]
QueryAnalyzerService.cs:488-497	ExtractEmployeeStatusFromQuestion	"Kontrak", "Tetap"
QueryAnalyzerService.cs:529-541	ExtractApprovalFromQuestion	"Pending", "Disetujui", "Ditolak"
QueryAnalyzerService.cs:788-838	BuildSopKeyword, BuildProfileKeyword	Mapping query → keyword tetap. Contoh: "kecepatan" → "kendaraan 20 km/jam area produksi"
Konsekuensi: Dokumen baru dengan divisi/jabatan/lokasi berbeda: sistem tidak bisa mengekstrak entitas tersebut.

TINGGI — Retrieval routing: 16 cabang if/else

[RetrievalService.cs:65-246]  — cascade if/else berbasis 16 kondisi dari RagQueryAnalysis
Urutan penting dan rentan:

Nik → exact payload search
MaintenanceCode → exact payload search
Date → exact payload search
IsAuditQuery → scroll by recordType="audit" (SELALU, tanpa threshold)
IsSopQuery → scroll by recordType="sop" + keyword
IsProfileQuery → scroll by recordType="profile" + keyword
IsEmployeeQuery + Division → payload filter
… (8 cabang lagi untuk employee/overtime/maintenance)
LooksLikePersonName → exact name search
Fallback: StructuredEntityResolver (fuzzy matching seluruh cache Qdrant)
Fallback: GenericRecordType → scroll all by type
Fallback terakhir: vector search (RetrievalService.cs:273-290)
Masalah struktural: Vector search (satu-satunya semantic search) adalah opsi terakhir, bukan jalur utama. Jika query mengandung keyword "maintenance" tapi bertanya soal SOP, query akan masuk ke path maintenance bukan SOP.

TINGGI — Chunking hanya untuk satu dokumen dummy
File:Line	Masalah
ChunkingService.cs:9	Pattern split hanya mengenali 6 section header persis ("1. Profil Perusahaan", dll.)
TextNormalizer.cs:103-193	NormalizeEmployeeTable, NormalizeOvertimeTable, NormalizeMaintenanceTable — regex dengan nama divisi, jabatan, peralatan, lokasi yang semuanya hardcoded ke dummy dataset
ChunkingService.cs:55-58	Dokumen yang tidak cocok → 1 chunk besar → gagal embedding Ollama (BadRequest)
TINGGI — AnswerFormatterService: template answer verbatim dari data dummy
File:Line	Deskripsi
AnswerFormatterService.cs:57-191	TryBuildOperationalRiskAnswer — 9 ContainsAny terhadap konten chunk untuk membangun 6 bullet
AnswerFormatterService.cs:369-372	Regex verbatim: @"hanya\s+dapat\s+diakses\s+(?:oleh\s+)?personel\s+HSSE\s+dan\s+Maintenance"
AnswerFormatterService.cs:357-395	Tank access branching — "HSSE" boleh, "Maintenance" boleh, "IT" tidak boleh (hardcoded ke dummy data)
AnswerFormatterService.cs:429-447	BuildSopListAnswer — judul "SOP Keamanan Area Kilang" hardcoded
AnswerFormatterService.cs:233-296	BuildAuditAnswer — regex: @"\b(\d+)\s+pelanggaran\s+minor\b", @"\b(\d+)\s+anomali\s+suhu\b"
SEDANG — StructuredEntityResolver: thresholds magic number
File:Line	Deskripsi
StructuredEntityResolver.cs:346-365	Fuzzy threshold 0.85 (multi-token) / 0.90 (single-token) — tidak di options
StructuredEntityResolver.cs:430-471	Priority scores: name=1000, equipment=900, … — tidak terdokumentasi
StructuredEntityResolver.cs:10-35	27 "generic value" yang difilter: "data", "karyawan", "divisi", "lokasi", …
SEDANG — Semantic search terlalu terbatas

// RetrievalService.cs:591-621
private static HashSet<string> GetAllowedSemanticRecordTypes(RagQueryAnalysis analysis)
{
    if (IsGeneralSummaryQuery(...))  return { "sop", "audit", "profile" };
    if (IsSafetyRiskOperationalQuery(...)) return { "sop", "audit" };
    if (!string.IsNullOrWhiteSpace(analysis.TargetRecordType)) return { targetRecordType };
    return empty; // any type
}
SemanticTopK=5, SemanticScoreThreshold=0.55, SemanticMaxContextChunks=3 — semantic path hanya mengembalikan 3 chunk, dengan threshold 0.55 yang cukup longgar tapi setelah difilter sering jadi 0.

RENDAH
File:Line	Deskripsi
QdrantScrollClient.cs:288-308	Stop word "pertamina" hardcoded — domain specific
PromptBuilderService.cs:38	"jika context berisi Data Karyawan, tampilkan semua field: NIK, Nama, Divisi, Jabatan, Shift, Status" — unconditional, menyebabkan semua field ditampilkan meski user hanya tanya divisi
QueryAnalyzerService.cs:57-72	NIK pattern RU6-XXXX dan maintenance code MT-XXX — tidak bisa diganti format lain
5. Flow Chat
Query pre-processing: Hanya .Trim() (RagChatService.cs:32). Tidak ada query rewriting, tidak ada HyDE, tidak ada step-back prompting.

Conversation history/memory: Tidak ada. Setiap request independent — tidak ada session, tidak ada sliding window, tidak ada entity memory.

Context assembly: PromptBuilderService.Build (PromptBuilderService.cs:8) — join chunks dengan format:


[Sumber N]
DocumentTitle: ...
RecordType: ...
SectionTitle: ...
Similarity: ...
Content: ...
Tidak ada compression, tidak ada ranking ulang sebelum dikirim ke LLM. Policy query menggunakan BuildPolicyGroundedPrompt (RagChatService.cs:121-123).

Citation/sumber: GetSources mengambil DocumentTitle yang distinct dari chunks — hanya nama file, tidak ada halaman atau batas chunk (RagChatService.cs:159-166).

LLM call: POST /api/generate ke Ollama, stream=false (OllamaService.cs:78). Tidak ada streaming ke client.

"Tidak ada konteks": IsNotFoundAnswer memeriksa apakah LLM menjawab dengan frasa "Maaf, saya tidak menemukan informasi tersebut" → kembalikan NotFoundResponse (RagChatService.cs:128-136). Rentan: frasa berbeda dari LLM tidak terdeteksi.

6. Gap Analysis: Kondisi vs. Best Practice RAG
Dimensi	Kondisi Sekarang	Best Practice	Gap
Query understanding	30+ keyword array + regex → 24 binary field flags	LLM slot-filling, intent classification, NER	Keyword miss = wrong path. "pegawai yang kerja malam" tidak trigger IsEmployeeQuery + Shift
Search strategy	Payload filter (scroll) = jalur utama, vector search = last resort	Hybrid: dense + sparse sebagai jalur utama, filter sebagai post-processing	Dense vector tidak pernah dipakai untuk structured data
Sparse/BM25	Tidak ada	BM25 via fastembed/SPLADE sebagai sparse vector	Exact term match (nama, kode) bergantung pada payload index, bukan ranking relevansi
Reranking	Tidak ada	Cross-encoder (ms-marco) setelah top-K retrieval	Chunk yang muncul pertama belum tentu paling relevan
Chunking	Structure-based hardcoded ke 6 section dummy	Semantic/recursive chunking, sentence-boundary, page-aware	Dokumen generik → 1 chunk besar → embedding gagal
Conversation memory	Tidak ada	3-5 turn sliding window, entity memory	Multi-turn chatbot mustahil
Streaming	Tidak ada (stream=false)	SSE/WebSocket streaming	UX buruk untuk response panjang
Answer generation	DeterministicTemplate (template hardcoded) ATAU raw LLM	Always LLM dengan strict grounding	Template answer tidak bisa adaptasi ke dokumen baru
Deduplication ingest	Tidak ada delete by documentId	Upsert by documentId → delete old chunks	Re-ingest = duplikasi
Evaluasi	Tidak ada	RAGAS / TruLens (retrieval recall, answer faithfulness)	Tidak bisa mengukur regresi
7. Rekomendasi Terprioritas
Quick Wins (efek langsung, tanpa breaking change)
#	Aksi	File:Line	Dampak
QW-1	Hapus instruksi "tampilkan semua field" unconditional di prompt	PromptBuilderService.cs:38	Query "sinta bekerja di divisi apa" tidak lagi mengembalikan semua field
QW-2	Naikkan SemanticTopK dari 5 → 15-20 di appsettings	appsettings.json:19	Lebih banyak kandidat chunk untuk semantic path
QW-3	Naikkan SemanticMaxContextChunks dari 3 → 5	appsettings.json:21	LLM mendapat lebih banyak konteks
QW-4	Hapus "pertamina" dari QdrantScrollClient stop words	QdrantScrollClient.cs:302	Stop word domain-specific tidak menyaring query valid
QW-5	Ekspos fuzzy threshold StructuredEntityResolver ke options	StructuredEntityResolver.cs:346-365	Tunable tanpa recompile
Perubahan Struktural
#	Aksi	Dampak	Effort
S-1	Generic chunking: tambah SplitBySize (3000 chars, 400 overlap) sebagai fallback di ChunkingService	Ingest dokumen non-dummy bisa jalan	Medium
S-2	Jadikan semantic search jalur pertama untuk dokumen type "document" — payload filter hanya untuk structured data (employee/overtime/maintenance)	Dokumen generik dijawab via vector search, bukan keyword cascade	Medium
S-3	Ganti AnswerFormatterService template untuk dokumen generic → gunakan LLM full untuk semua recordType="document"	Tidak lagi terikat ke template dummy	Medium
S-4	Tambah sparse vector (BM25 via fastembed) + Qdrant hybrid search (RRF fusion)	Exact term + semantic dalam satu query	High
S-5	Query understanding via LLM: ganti keyword cascade → JSON slot-filling	Sinonim, bahasa informal, multi-intent tertangkap	High
S-6	Delete by documentId saat re-ingest	Tidak ada duplikasi di Qdrant	Medium
8. Rencana Migrasi Bertahap

TAHAP 0 — Stabilisasi (tidak breaking)
  0a. QW-1: hapus prompt "tampilkan semua field"      → 1 baris edit
  0b. QW-2/QW-3: naikkan topK dan contextChunks       → appsettings only
  0c. QW-4: hapus "pertamina" dari stopwords          → 1 baris edit
  0d. Tambah SplitBySize di ChunkingService           → sudah ada di branch, re-apply

TAHAP 1 — Pisahkan jalur dokumen generik vs. structured
  1a. Tambah mode dispatch di RagChatService:
      - recordType="employee/overtime/maintenance" → legacy keyword path
      - recordType="document/sop/audit/profile" → semantic path (vector search)
  1b. Tidak mengubah QueryAnalyzerService atau AnswerFormatterService
  1c. Test: query ke dokumen PDF generik → pure vector response

TAHAP 2 — Hybrid search (dense + sparse)
  2a. Tambah sparse_vectors di QdrantCollectionService
  2b. Embed sparse via fastembed di EmbeddingIngestionService
  2c. Update UpsertChunkAsync: tambah sparse vector
  2d. Update SearchSemanticAsync: hybrid query dengan RRF fusion
  2e. Re-ingest semua dokumen
  → Structured query tetap pakai payload filter

TAHAP 3 — Ganti query understanding
  3a. Buat QueryUnderstandingService (LLM JSON schema):
      { intent, entities: [{type, value}], domain }
  3b. A/B via flag di RagModeOptions:
      Mode="Legacy" → QueryAnalyzerService
      Mode="Semantic" → QueryUnderstandingService
  3c. Jalankan keduanya secara parallel, bandingkan di log
  3d. Setelah stabil: default ke Semantic

TAHAP 4 — Cleanup legacy
  4a. Pindahkan AnswerFormatterService template ke LegacyAnswerFormatter
  4b. QueryAnalyzerService dan TextNormalizer dummy-specific bisa didelete
      setelah mode Legacy tidak dipakai
  → Zero breaking change sampai titik ini
  