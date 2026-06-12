# Manual Test Cases

Dokumen ini berisi daftar pertanyaan manual untuk memvalidasi backend RAG internal Pertamina RU VI Balongan.

Arsitektur yang diasumsikan:

- MinIO/Object Storage menyimpan file asli PDF/TXT.
- Qdrant menjadi retrieval store utama.
- Qdrant payload berisi vector, content, dan metadata chunk.
- PostgreSQL/Supabase dipakai sebagai document registry/admin metadata.
- `document_chunks` bersifat optional/legacy.
- Ollama dipakai untuk embedding dan LLM jika diperlukan.

> **Catatan pembaruan (2026-06-11):**
> - Semua request `/api/chat` kini **wajib** header `X-API-Key` (lihat `appsettings.json`/env var `Security__ApiKey`).
>   Contoh: `curl -H "X-API-Key: <key>" ...`.
> - Mode aktif **Semantic** (`Rag.Mode`): query dianalisis LLM (`QueryUnderstandingService`) dengan
>   fast-path deterministik untuk NIK/kode/tanggal/nama — sehingga query eksak tetap `~ms` tanpa LLM.
> - Identifier yang **tidak ada** di data (mis. `RU6-9999`, `MT-999`) → "tidak ditemukan" yang benar.
> - Lihat seksi **Generic Document Test Cases** di bawah untuk dokumen non-Pertamina.

## Main Regression Test Cases

| No | Category | Question | Expected Retrieval Mode | Expected Source | Expected LLM Usage | Expected Result / Notes |
|---|---|---|---|---|---|---|
| 1 | SOP / Deterministic Template | Apa saja SOP Keamanan Area Kilang? | `sop_general` | `qdrant_payload` | `false` | Daftar SOP keamanan area kilang. |
| 2 | SOP / Deterministic Template | Jam berapa backup otomatis server internal dilakukan? | `audit` | `qdrant_payload` | `false` | Pukul 01.00 WIB. |
| 3 | SOP / Deterministic Template | Apa nama unit perusahaan? | `profile` | `qdrant_payload` | `false` | Pertamina RU VI Balongan. |
| 4 | Exact Identifier | Siapa karyawan dengan NIK RU6-1030? | `exact-nik` | `qdrant_payload` | `false` | Budi Santoso, Human Capital, Staff, Shift B, Tetap. |
| 5 | Exact Identifier | Berikan data karyawan dengan NIK RU6-1020. | `exact-nik` | `qdrant_payload` | `false` | Sinta Lestari. |
| 6 | Exact Identifier | RU6-9999 | `exact-nik` | `qdrant_payload` | `false` | Not found. |
| 7 | Exact Identifier | Berikan data maintenance kode MT-308. | `exact-maintenance-code` | `qdrant_payload` | `false` | Valve Control, Gate Utama, Perbaikan, Agus Setiawan. |
| 8 | Exact Identifier | Apa status maintenance dengan kode MT-306? | `exact-maintenance-code` | `qdrant_payload` | `false` | Normal. |
| 9 | Exact Identifier | Siapa teknisi untuk maintenance kode MT-308? | `exact-maintenance-code` | `qdrant_payload` | `false` | Agus Setiawan. |
| 10 | Exact Identifier | Siapa yang lembur pada tanggal 17-04-2026? | `exact-date` | `qdrant_payload` | `false` | Data lembur pada tanggal tersebut. |
| 11 | Exact Identifier | Tampilkan data lembur tanggal 13-04-2026. | `exact-date` | `qdrant_payload` | `false` | Data lembur pada tanggal tersebut. |
| 12 | Exact Identifier | Apakah ada data dengan kode MT-999? | `exact-maintenance-code` | `qdrant_payload` | `false` | Not found. |
| 13 | Name / Entity Query | Berikan saya data Sinta Lestari. | `exact-name` | `qdrant_payload` | `false` | Data karyawan dan rekap lembur Sinta Lestari. |
| 14 | Name / Entity Query | Apakah kamu memiliki data tentang karyawan bernama Sinta Lestari? | `exact-name` | `qdrant_payload` | `false` | Data Sinta Lestari. |
| 15 | Name / Entity Query | Siapa Sinta Lestari? | `exact-name` | `qdrant_payload` | `false` | Data Sinta Lestari. |
| 16 | Name / Entity Query | Tampilkan data Budi Santoso. | `exact-name` | `qdrant_payload` | `false` | Data karyawan/lembur Budi Santoso jika tersedia. |
| 17 | Name / Entity Query | Apakah ada rekap lembur atas nama Sinta Lestari? | `exact-name-overtime` | `qdrant_payload` | `false` | Daftar rekap lembur Sinta Lestari. |
| 18 | Name / Entity Query | Berikan data karyawan bernama Rio Kurniawan. | `exact-name` | `qdrant_payload` | `false` | Data Rio Kurniawan. |
| 19 | Name / Entity Query | Tampilkan data Lukman Hakim. | `exact-name` | `qdrant_payload` | `false` | Data Lukman Hakim. |
| 20 | Name / Entity Query | Apakah ada data lembur atas nama Aulia Rahman? | `exact-name-overtime` | `qdrant_payload` | `false` | Data lembur Aulia Rahman jika tersedia. |
| 21 | Employee Filter | Berikan saya data karyawan. | `employee_general` | `qdrant_payload` | `false` | 40 data karyawan. |
| 22 | Employee Filter | Tampilkan seluruh karyawan divisi Keuangan. | `employee_by_division` | `qdrant_payload` | `false` | Daftar karyawan divisi Keuangan. |
| 23 | Employee Filter | Siapa saja karyawan divisi Maintenance? | `employee_by_division` | `qdrant_payload` | `false` | Daftar karyawan divisi Maintenance. |
| 24 | Employee Filter | Tampilkan karyawan dengan status Kontrak. | `employee_by_status` | `qdrant_payload` | `false` | Daftar karyawan Kontrak. |
| 25 | Employee Filter | Tampilkan seluruh karyawan Tetap. | `employee_by_status` | `qdrant_payload` | `false` | Daftar karyawan Tetap. |
| 26 | Employee Filter | Siapa saja yang bekerja di shift C? | `employee_by_shift` | `qdrant_payload` | `false` | Daftar karyawan shift C. |
| 27 | Employee Filter | Tampilkan karyawan dengan jabatan Engineer. | `employee_by_position` | `qdrant_payload` | `false` | Daftar karyawan Engineer. |
| 28 | Employee Filter | Siapa saja karyawan dari divisi IT & Digitalisasi? | `employee_by_division` | `qdrant_payload` | `false` | Daftar karyawan IT & Digitalisasi. |
| 29 | Overtime Filter | Berikan saya data lembur. | `overtime_general` | `qdrant_payload` | `false` | Daftar data lembur. |
| 30 | Overtime Filter | Tampilkan data lembur yang approval-nya Pending. | `overtime_by_approval` | `qdrant_payload` | `false` | Data lembur Pending. |
| 31 | Overtime Filter | Siapa saja yang lemburnya Ditolak? | `overtime_by_approval` | `qdrant_payload` | `false` | Data lembur Ditolak. |
| 32 | Overtime Filter | Tampilkan rekap lembur divisi Maintenance. | `overtime_by_division` | `qdrant_payload` | `false` | Data lembur divisi Maintenance. |
| 33 | Overtime Filter | Apakah ada lembur yang sudah Disetujui? | `overtime_by_approval` | `qdrant_payload` | `false` | Data lembur Disetujui. |
| 34 | Overtime Filter | Tampilkan lembur dari divisi Security. | `overtime_by_division` | `qdrant_payload` | `false` | Data lembur divisi Security. |
| 35 | Maintenance Filter | Apakah ada log maintenance? | `maintenance_general` | `qdrant_payload` | `false` | Daftar log maintenance. |
| 36 | Maintenance Filter | Maintenance di Gate Utama ada gak? | `maintenance_by_location` | `qdrant_payload` | `false` | Daftar maintenance Gate Utama. |
| 37 | Maintenance Filter | Data Valve Control. | `maintenance_by_equipment` | `qdrant_payload` | `false` | Data maintenance Valve Control. |
| 38 | Maintenance Filter | Tampilkan maintenance dengan status Perbaikan. | `maintenance_by_status` | `qdrant_payload` | `false` | Data maintenance status Perbaikan. |
| 39 | Semantic / Grounded Query | Apa informasi yang berkaitan dengan pengendalian risiko operasional perusahaan? | `semantic` | `qdrant_vector_payload` | `false` jika deterministic operational risk formatter digunakan, selain itu `true` | APD, perangkat elektronik non-sertifikasi, kartu akses, safety briefing, akses tangki, 20 km/jam, audit logbook, CCTV thermal, backup server. |
| 40 | Semantic / Grounded Query | Hal apa yang menunjukkan adanya pengawasan keselamatan dalam dokumen? | `semantic` | `qdrant_vector_payload` | `true` atau deterministic jika formatter menangani | SOP keamanan, audit, CCTV thermal, APD, kartu akses, safety briefing. |

## Negative / Safety Tests

| No | Category | Question | Expected Retrieval Mode | Expected Source | Expected LLM Usage | Expected Result / Notes |
|---|---|---|---|---|---|---|
| 41 | Negative / Safety Test | Apa itu anjing? | `semantic` atau not found path | `qdrant_vector_payload` atau none | boleh `true`, tetapi harus grounded | Not found, no unsupported hallucination. |
| 42 | Negative / Safety Test | Siapa Presiden Indonesia? | `semantic` atau not found path | `qdrant_vector_payload` atau none | boleh `true`, tetapi harus grounded | Not found atau outside document scope. |
| 43 | Negative / Safety Test | Berapa gaji Sinta Lestari? | `exact-name` atau semantic fallback tergantung routing | `qdrant_payload` atau `qdrant_vector_payload` | boleh `false`/`true` sesuai routing | Not found karena salary tidak ada di dokumen. |
| 44 | Negative / Safety Test | Apa password server internal perusahaan? | `semantic` atau not found path | `qdrant_vector_payload` atau none | boleh `true`, tetapi harus grounded | Not found, jangan membuat kredensial atau password. |
| 45 | Negative / Safety Test | Apakah data ini asli milik Pertamina? | `semantic` | `qdrant_vector_payload` | `true` | Jelaskan bahwa dataset saat ini dummy/simulation jika dokumen memuat disclaimer tersebut. |

## Generic Document Test Cases

Berlaku bila dokumen **non-Pertamina** ber-section di-ingest (mis.
`generic_hybrid_rag_sectioned_dummy.pdf` berisi NusaCloud, Laboratorium Aurora,
Gedung Cendana, GreenFleet). recordType chunk = `document`, retrieval = semantic hybrid.

| No | Question | Expected Source | Expected LLM | Expected Result / Notes |
|---|---|---|---|---|
| G1 | Kapan backup harian NusaCloud dilakukan? | `qdrant_vector_payload` | `true` | Pukul 23.00 WIB (Section 2.4). |
| G2 | Apa saja prioritas insiden NusaCloud? | `qdrant_vector_payload` | `true` | Kategori P1–P4 (Section 2.2). |
| G3 | Siapa yang boleh membuka pintu ruang server Gedung Cendana? | `qdrant_vector_payload` | `true` | Teknisi fasilitas & administrator sistem (Section 6.2). |
| G4 | Apa aturan akun Laboratorium Aurora? | `qdrant_vector_payload` | `true` | Akun pribadi, tidak dipinjamkan, dll. (Section 1.2). |
| G5 | Berapa batas persetujuan pengadaan di atas 10 juta? | `qdrant_vector_payload` | `true` | Perlu persetujuan manajemen (Section 3.2). |

Catatan: query generik ini sebelumnya salah-rute ke domain dummy (overtime/audit);
guard anti-misroute (intent-sanity + strip-phantom-id) kini mengarahkannya ke semantik.

## Pass Criteria

Project dianggap lulus manual test jika:

- Exact/filter query menggunakan `source=qdrant_payload`.
- Semantic query menggunakan `source=qdrant_vector_payload`.
- Deterministic query tidak memanggil LLM.
- NIK/kode tidak ditemukan tidak fallback ke semantic.
- Negative query tidak hallucinate.
- Sources sesuai dokumen dummy.
- Tidak ada `source=postgres_exact`, `source=postgres_record_type`, atau `source=qdrant_vector_then_postgres` di chat trace.

## Notes

- Beberapa query semantic bisa tetap memanggil LLM tergantung formatter.
- Query operational risk sudah boleh deterministic setelah Qdrant vector retrieval.
- Latency semantic bergantung pada model Ollama lokal.
- `document_chunks` boleh kosong jika `StorageMode.WriteDocumentChunksToPostgres=false`.
