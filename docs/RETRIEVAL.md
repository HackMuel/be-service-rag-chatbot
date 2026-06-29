# Three-Level Retrieval — be_service

Sistem memilih salah satu dari tiga level retrieval per query. Pemilihan dikendalikan
`RagChatService` (fast-path deterministik → `QueryUnderstandingService` LLM + guard
anti-misroute → fallback `QueryAnalyzerService` keyword). Detail planner & guard ada di
[CODEBASE_GUIDE.md](CODEBASE_GUIDE.md) dan [PRD.md](PRD.md).

## Level 1 — Exact / Structured Retrieval

Untuk data terstruktur: NIK, maintenance code, date, name, division, shift, employeeStatus,
position, approval, location, equipment, technician, maintenanceStatus.

```text
QueryAnalyzer/Understanding → StructuredEntityResolver (jika perlu) → Qdrant payload filter
  → AnswerFormatterService → Response
```

Karakteristik: filter payload Qdrant, **tanpa LLM**, tidak memakai `document_chunks`,
cepat & deterministic.

Contoh: `Siapa karyawan dengan NIK RU6-1030?`, `berikan saya data sinta lestari`,
`tampilkan karyawan divisi Keuangan`, `siapa aja yang shift C?`, `Berikan data maintenance kode MT-308`.

## Level 2 — Deterministic Template

Untuk pertanyaan pasti & sering: SOP keamanan, backup server, kepatuhan APD, kecepatan
kendaraan, profil perusahaan, audit/profile facts.

```text
Qdrant payload filter by recordType → AnswerFormatterService (template) → Response
```

Karakteristik: payload Qdrant, **tanpa LLM** bila template cocok, mengurangi latency & hallucination.

Contoh: `Apa saja SOP Keamanan Area Kilang?`, `Jam berapa backup otomatis server internal dilakukan?`,
`Apa tingkat kepatuhan APD?`, `Berapa kapasitas produksi Pertamina RU VI Balongan?`.

## Level 3 — Grounded Semantic / Policy Answer

Untuk pertanyaan fleksibel, interpretatif, policy/izin, prosedur, risiko, atau ringkasan.

```text
Ollama embedding (dense) + sparse BM25 → Qdrant hybrid search (RRF) with payload
  → semantic context filtering/reranking → clean context
  → PromptBuilderService (grounded prompt) → Ollama LLM → Response
```

Karakteristik:
- Hybrid search Qdrant (`dense` + `sparse`, fusi RRF) dengan payload.
- Context dibersihkan dari noise/disclaimer sebelum masuk LLM.
- LLM **wajib** menjawab hanya dari context; dilarang membuat izin/aturan/informasi baru.
- Bila context tak cukup: `Maaf, saya tidak menemukan informasi tersebut.`

Contoh: `Apakah orang IT boleh masuk area penyimpanan?`,
`Apa risiko membawa perangkat elektronik biasa ke area kilang?`,
`Ringkas isi dokumen ini dari sisi keamanan dan operasional.`
