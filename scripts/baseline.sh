#!/usr/bin/env bash
# baseline.sh — rekam/jalankan ulang 17 query BASELINE.md sebagai gate migrasi.
#
# Pakai:
#   export API_KEY=<isi Security:ApiKey>
#   ./scripts/baseline.sh                 # tulis ke tests/baseline/golden/
#   ./scripts/baseline.sh tests/tmp       # tulis ke folder lain (untuk bandingkan dua run)
#
# Prasyarat: server jalan + data sudah di-ingest bersih (Tahap 0.1). Butuh `jq`.
# Gate: commit golden sekali, lalu setelah tiap langkah refactor jalankan ulang
#       (timpa folder yang sama) dan jalankan:  git diff tests/baseline/golden

set -euo pipefail

BASE="${BASE:-http://localhost:5057}"
: "${API_KEY:?set API_KEY dulu, mis. export API_KEY=47585ade014ee0d3457d7dd80eeafe0d5aaa56c75edf77e06a3915aab108c386}"
OUT="${1:-tests/baseline/golden}"
mkdir -p "$OUT"

ids=(A1 A2 A3 B1 B2 B3 B4 B5 C1 C2 C3 D1 D2 D3 E1 E2 F1)
queries=(
  "data karyawan NIK RU6-0001"
  "log maintenance kode MT-001"
  "rekap lembur tanggal 01-01-2025"
  "siapa saja karyawan divisi IT?"
  "karyawan shift A ada berapa?"
  "karyawan dengan status kontrak"
  "siapa supervisor di perusahaan ini?"
  "rekap lembur yang sudah disetujui"
  "sinta lestari bekerja di divisi apa?"
  "tampilkan semua data karyawan sinta lestari"
  "siapa teknisi yang menangani MT-002?"
  "apa aturan APD di area produksi?"
  "siapa yang boleh masuk area tangki penyimpanan?"
  "apa saja SOP keamanan yang berlaku?"
  "berapa persen tingkat kepatuhan APD?"
  "apa nama unit perusahaan ini?"
  "bagaimana prosedur operasional kilang?"
)

for i in "${!ids[@]}"; do
  id="${ids[$i]}"
  q="${queries[$i]}"
  printf '[%-3s] %s\n' "$id" "$q"

  # jq -Rn meng-escape query dengan aman (ada tanda ?, dll.)
  body="$(jq -Rn --arg m "$q" '{message:$m}')"

  # jq -S menyortir key → diff stabil. notFound = sinyal stabil walau prosa LLM berubah.
  if [[ "$id" == "D2" || "$id" == "F1" ]]; then
    filter='{ sources: (.sources | sort) }'
  else
    filter='{ answer, sources, notFound: ((.answer // "") | test("tidak menemukan informasi")) }'
  fi

  curl -s -X POST "$BASE/api/chat" \
       -H "Content-Type: application/json" \
       -H "X-API-Key: $API_KEY" \
       -d "$body" \
  | jq -S "$filter" > "$OUT/$id.json"
done

echo "✓ 17 query tersimpan di $OUT/"