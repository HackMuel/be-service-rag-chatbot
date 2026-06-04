# Query Baseline Set — Tahap 0

Branch: `query-understanding-layer`  
Dibuat sebelum implementasi Tahap 0 selesai; diisi ulang setelah server berjalan.

Gunakan file ini untuk mendeteksi regresi di setiap tahap berikutnya.
Jalankan semua query ke `POST /api/chat { "message": "<query>" }` dan catat respons aktual.

---

## Format kolom
- **Query**: teks yang dikirim
- **Expected mode**: retrieval mode yang diharapkan (dari log `RETRIEVAL_TRACE`)
- **Expected answer**: perilaku jawaban yang diharapkan
- **Aktual sebelum T0**: diisi saat baseline diambil
- **Aktual setelah T0**: diisi setelah Tahap 0 selesai

---

## A — Exact Lookup (structured, identifier)

| # | Query | Expected mode | Expected answer |
|---|-------|---------------|-----------------|
| A1 | `data karyawan NIK RU6-0001` | `exact-nik` | Semua field karyawan dengan NIK tersebut |
| A2 | `log maintenance kode MT-001` | `exact-maintenance-code` | Detail record maintenance MT-001 |
| A3 | `rekap lembur tanggal 01-01-2025` | `exact-date` | Semua lembur di tanggal itu |

---

## B — Structured Filter (employee / overtime / maintenance)

| # | Query | Expected mode | Expected answer |
|---|-------|---------------|-----------------|
| B1 | `siapa saja karyawan divisi IT?` | `employee_by_division` | Daftar karyawan IT & Digitalisasi |
| B2 | `karyawan shift A ada berapa?` | `employee_by_shift` | Daftar/jumlah karyawan shift A |
| B3 | `karyawan dengan status kontrak` | `employee_by_status` | Daftar karyawan kontrak |
| B4 | `siapa supervisor di perusahaan ini?` | `employee_by_position` | Daftar karyawan jabatan Supervisor |
| B5 | `rekap lembur yang sudah disetujui` | `overtime_by_approval` | Daftar lembur dengan Approval=Disetujui |

---

## C — Person Name Lookup

| # | Query | Expected mode | Expected answer |
|---|-------|---------------|-----------------|
| C1 | `sinta lestari bekerja di divisi apa?` | `exact-name` | **Hanya divisi** — bukan semua field (ini yang diperbaiki di T0) |
| C2 | `tampilkan semua data karyawan sinta lestari` | `exact-name` | Semua field karena user minta "semua data" |
| C3 | `siapa teknisi yang menangani MT-002?` | `maintenance_by_technician` atau `exact-name` | Nama teknisi record MT-002 |

---

## D — SOP & Policy

| # | Query | Expected mode | Expected answer |
|---|-------|---------------|-----------------|
| D1 | `apa aturan APD di area produksi?` | `sop` | Aturan APD dari SOP |
| D2 | `siapa yang boleh masuk area tangki penyimpanan?` | `sop`, answerLevel `PolicyGrounded` | Hanya HSSE dan Maintenance (berdasarkan context SOP) |
| D3 | `apa saja SOP keamanan yang berlaku?` | `sop_general` | Daftar SOP yang ada di dokumen |

---

## E — Audit & Profile

| # | Query | Expected mode | Expected answer |
|---|-------|---------------|-----------------|
| E1 | `berapa persen tingkat kepatuhan APD?` | `audit`, DeterministicTemplate | Persentase kepatuhan dari catatan audit |
| E2 | `apa nama unit perusahaan ini?` | `profile` | Nama unit dari profil perusahaan |

---

## F — Generic / Semantic (jalur yang masih lemah sebelum T1)

| # | Query | Expected mode | Expected answer |
|---|-------|---------------|-----------------|
| F1 | `bagaimana prosedur operasional kilang?` | `semantic` (fallback) | Jawaban dari chunk relevan; mungkin kosong jika belum ada dokumen generik |

---

## Catatan regresi

Isi kolom ini setelah setiap tahap:

| Tahap | Query # | Status | Catatan |
|-------|---------|--------|---------|
| T0    | C1      | ✅ fixed | Sebelumnya menampilkan semua field; setelah T0 hanya divisi |
| T0    | —       | —      | — |

---

## Pengukuran Mode Semantic (2026-06-04, default Rag.Mode=Semantic, fallback aktif)

17 query BASELINE dijalankan dengan `QueryUnderstandingService` (LLM qwen2.5:1.5b) sebagai
analyzer default, fallback ke `QueryAnalyzerService` dipertahankan. Marker log:
`QUS_SOURCE`, `QUS_FALLBACK`, `QUS_VS_LEGACY`.

**Fallback rate:** 14/17 ditangani LLM tanpa fallback; 3/17 jatuh ke fallback
(D3, E2, F1 — semua `reason=parse`, output JSON LLM tak terparse). Ketiganya tetap
menghasilkan jawaban benar via legacy → fallback bekerja sebagaimana mestinya.

**Catatan data:** A1 (RU6-0001), A2 (MT-001), A3 (01-01-2025), C3 (MT-002) memakai
identifier yang TIDAK ADA di data terindeks (NIK mulai RU6-1001, kode MT-300+, tanggal
April 2026). "Not found" untuk keempatnya benar — bukan regresi.

**Regresi Semantic vs Hybrid (dikonfirmasi langsung di mode Hybrid):**

| Query | Semantic (LLM) | Hybrid (legacy) | Sebab |
|-------|----------------|-----------------|-------|
| B4 `siapa supervisor di perusahaan ini?` | ❌ "tidak ditemukan" (SemanticGrounded, vector path) | ✅ 2 Supervisor (Agus, Nadia) | LLM gagal ekstrak position=Supervisor → entities=0 |
| B5 `rekap lembur yang sudah disetujui` | ❌ "tidak ditemukan" | ✅ 15 record Disetujui | LLM gagal ekstrak approval=Disetujui |
| D1 `apa aturan APD di area produksi?` | ❌ jawab 97,4% kepatuhan (intent=audit) | ✅ aturan SOP APD | LLM salah intent: audit, bukan sop |

**Tanpa regresi (Semantic = benar):** B1, B2, B3 (filter divisi/shift/status),
C1, C2 (exact-name), D2 (policy tangki), D3, E1, E2, F1.

**Kesimpulan:** 3 regresi nyata, semua akibat LLM analyzer (slot-filling kategori
& intent), bukan retrieval. Fallback menutup 3 kasus parse-fail tapi TIDAK menutup
B4/B5/D1 karena LLM mengembalikan JSON valid tapi salah isi. Belum layak melepas
fallback / menghapus legacy. Perbaikan kandidat: perkuat few-shot prompt untuk
position & approval, pertegas batas audit vs sop.

----------------------------------------------------------------------------- |

POST /api/chat
↓
Analisis pertanyaan user
↓
Apakah pertanyaan dilarang?
├─ Ya  → jawab aman, berhenti
└─ Tidak
   ↓
   Apakah pertanyaan semantic?
   ├─ Ya  → dense + sparse Qdrant search → LLM
   └─ Tidak → payload/structured retrieval → deterministic answer atau LLM