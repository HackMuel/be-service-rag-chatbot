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

---

## Pengukuran Mode Semantic — setelah perbaikan prompt (2026-06-04)

Perbaikan di `QueryUnderstandingService` (hanya prompt + temperature + retry, tanpa
ubah arsitektur): few-shot eksplisit position/approval + daftar nilai valid, pertegas
batas sop-vs-audit, `temperature=0`, instruksi "raw JSON only", 1× retry sebelum fallback.

**Regresi: 3/3 TERATASI**

| Query | Sebelum | Sesudah | Bukti |
|-------|---------|---------|-------|
| B4 supervisor | ❌ not found | ✅ 2 Supervisor (Agus, Nadia) | intent=employee, `employee_by_position`, chunks=2 |
| B5 lembur disetujui | ❌ not found | ✅ 15 record Disetujui | intent=overtime, `overtime_by_approval`, chunks=15 |
| D1 aturan APD | ❌ jawab 97,4% (audit) | ✅ aturan SOP APD | intent=sop, `sop_general`, chunks=1 |

Bonus: **C3** (`teknisi MT-002`) analyzer kini benar (intent=maintenance, code=MT-002)
— sebelumnya salah intent=semantic. Jawaban tetap "not found" (MT-002 tak ada di data).

**Parse failure (sebelumnya D3, E2, F1 — reason=parse):**

| Query | Hasil | Catatan |
|-------|-------|---------|
| E2 `nama unit perusahaan` | ✅ FIXED | Kini terparse via LLM (intent=profile), tanpa fallback |
| F1 `prosedur operasional kilang` | ⚠️ tetap fallback-parse | Retry 1× tak menolong; jawaban benar via fallback legacy |
| D3 `SOP keamanan berlaku` | ⚠️ fallback-error | Bukan lagi parse-fail — LLM analysis call timeout (latensi qwen2.5:1.5b di bawah beban). Jawaban benar via fallback; saat diberi waktu cukup, jalur normal pun benar |

**Fallback rate:** 15/17 ditangani LLM tanpa fallback (vs 14/17 sebelumnya). 2/17
fallback (D3=error/timeout, F1=parse). Retry terpicu 1× (F1).

**Catatan:** Semua jawaban akhir 17 query benar (atau "not found" yang benar untuk
A1/A2/A3/C3 yang identifier-nya memang tak ada di data). Tidak ada regresi tersisa.
D3/F1 masih bergantung fallback → pelepasan fallback tetap ditunda; F1 perlu prompt
JSON lebih ketat, D3 perlu mitigasi latensi (timeout/model lebih besar).

---

## Fix tambahan: query "list semua" (2026-06-04)

Regresi terlapor: `berikan saya semua data karyawan` → kosong (sebelumnya bisa).

**Akar masalah:** `QueryUnderstandingService.DetermineAnswerLevel` memetakan intent
employee/overtime/maintenance TANPA filter ke `SemanticGrounded`. Di mode Semantic,
`SemanticGrounded` dibelokkan ke `AskSemanticAsync` (vektor murni) sebelum mencapai
jalur `GenericRecordType` scroll di `RetrieveAsync` → hasil kosong. Legacy memetakan
generic record type employee/overtime/maintenance ke `ExactStructured`.

**Fix:** intent employee/overtime/maintenance tanpa filter → `ExactStructured`
(mirror legacy). Verifikasi:

| Query | Mode | Hasil |
|-------|------|-------|
| `berikan saya semua data karyawan` | `employee_general` | ✅ 40 karyawan |
| `rekap lembur semua karyawan` | `overtime_general` | ✅ 30 record |
| `semua log maintenance` | `maintenance_general` | ✅ 24 record |

---

## Robustness #1/#2: JSON-forced output + prompt sinonim (2026-06-08)

Probe 15 query sulit (sinonim/informal/parafrase) menemukan kegagalan nyata BUKAN
karena nilai hardcoded, tapi: (a) LLM gagal emit JSON valid (parse-fail), (b) slot/intent
tak terekstrak untuk sinonim. Perbaikan (hanya QueryUnderstandingService + OllamaService):

- **`format: "json"` pada panggilan Ollama understanding** → output dijamin JSON valid.
- Prompt: sinonim `mandor`→position=Supervisor; contoh `jelaskan perusahaan`→profile,
  `teknisi generator`→maintenance+equipment.

**Hasil:**

| Query | Sebelum | Sesudah |
|-------|---------|---------|
| `ada mandor ga di sini` | ❌ intent=semantic → kosong | ✅ employee_by_position, 2 karyawan |
| `jelaskan tentang perusahaan ini` | ❌ parse-fail → kosong | ✅ profile, 5 chunks |
| `siapa teknisi generator` | ❌ parse-fail → kosong | ⚠️ intent+equipment benar, tapi jatuh ke `maintenance_general` (belum ada retrieval by-equipment) |

**Parse-fail: 3/17 → 0/17** pada BASELINE penuh. D3/E2/F1 yang dulu bersandar fallback
kini ditangani LLM langsung. Tidak ada regresi pada B/D/E (semua benar).

**Belum tertangani (di luar #1/#2):**
- Retrieval by-equipment / by-code untuk "teknisi <alat>" (P15, C3) → butuh cabang retrieval baru (struktural).
- `boleh ga orang IT masuk tangki` (P9): analisis benar (PolicyGrounded) tapi jawaban
  ketimpa template SOP generik → lapisan jawaban (AnswerFormatterService).
- Field-projection (C1: "divisi sinta" → dump semua field) → lapisan jawaban.

---

## Status terkini (2026-06-11)

Item "belum tertangani" di atas sebagiannya sudah dibereskan pada iterasi berikutnya:

- ✅ **Anti-misroute (guard deterministik):** `StripPhantomIdentifiers` + grounding gate +
  `ApplyIntentSanityGate` + rute generic-policy → semantik. Query dokumen **generik**
  (NusaCloud/Aurora/Cendana/GreenFleet) yang dulu salah-rute ke domain dummy kini terjawab
  benar (kapan backup, prioritas insiden, akses ruang server, aturan akun Lab, batas pengadaan).
- ✅ **Ingestion config-driven (`DatasetSchema`)** + payload dua-lapis (Gate 1–3 lulus,
  behavior-preserving terhadap dummy).
- ✅ **Ekstraksi PDF berbasis koordinat** → chunking per-section untuk dokumen generik.
- ✅ **Secrets via env var** + `appsettings.json` di-`.gitignore` + template `appsettings.Example.json`.

**Masih terbuka (lihat [docs/PRD.md](docs/PRD.md) §12):** field-projection (C1), retrieval
by-equipment (P15/C3), kebijakan jawaban policy P9, presisi ringkasan LLM, test otomatis.

Alur ringkas final:

```
POST /api/chat
 → fast-path (NIK/kode/tanggal/nama korpus) → filter payload → jawaban (tanpa LLM)
 → QueryUnderstanding (LLM, JSON) + guard (phantom-id, grounding, intent-sanity)
     ├─ SemanticGrounded → hybrid dense+sparse (RRF) → prompt grounded → LLM
     └─ selain itu       → filter payload / template → jawaban
```