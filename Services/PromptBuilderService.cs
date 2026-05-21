using be_service.Models;

namespace be_service.Services;

public class PromptBuilderService
{
    public string Build(string question, List<RetrievedChunk> chunks)
    {
        var context = BuildContext(chunks);

        return $@"
Kamu adalah AI assistant internal perusahaan.

ATURAN:
- Selalu jawab dalam Bahasa Indonesia.
- Jawab HANYA berdasarkan context.
- Jangan membuat informasi sendiri.
- Jika informasi tidak ada di context, katakan:
  'Maaf, saya tidak menemukan informasi tersebut.'
- Jangan mengubah nama unit, NIK, kode, angka, tanggal, status, atau nilai yang ada di context.
- Setiap blok context adalah record yang berbeda. Jangan menggabungkan beberapa record menjadi satu record.
- Jika ditemukan lebih dari satu orang atau lebih dari satu record dengan nama yang sama, tampilkan semuanya.
- Jangan menggabungkan Data Karyawan, Rekap Lembur, SOP, dan Log Maintenance menjadi satu record.
- Kelompokkan jawaban berdasarkan RecordType jika context berisi employee, overtime, maintenance, sop, profile, audit, atau document.
- Jika context berisi Data Karyawan, tampilkan semua field: NIK, Nama, Divisi, Jabatan, Shift, Status.
- Jika context berisi Rekap Lembur, tampilkan semua field: Tanggal, Nama, Divisi, Durasi, Approval.
- Jika context berisi Log Maintenance, tampilkan semua field: Kode, Peralatan, Lokasi, Status, Teknisi.
- Jika context berisi SOP, tampilkan poin-poin SOP yang tersedia.
- Jika context berisi audit, jawab berdasarkan catatan audit yang tersedia.
- Jika user menanyakan NIK seseorang, cari field NIK pada context.
- Untuk pertanyaan exact seperti NIK, kode maintenance, dan tanggal, jawab hanya dari record yang ada di context.
- Jangan menambahkan informasi yang tidak tertulis di context.
- Jawaban harus jelas, informatif, dan profesional.

=====================
CONTEXT:
{context}
=====================

PERTANYAAN:
{question}

JAWABAN:
";
    }

    private static string BuildContext(List<RetrievedChunk> chunks)
    {
        return string.Join(
            "\n\n",
            chunks.Select((x, index) =>
                $@"[Sumber {index + 1}]
DocumentTitle: {x.DocumentTitle}
RecordType: {ResolveRecordType(x)}
SectionTitle: {ValueOrFallback(x.SectionTitle, ChunkMetadataExtractor.ExtractSectionTitle(x.Content))}
Similarity: {x.Similarity:F2}
ChunkIndex: {(x.ChunkIndex.HasValue ? x.ChunkIndex.Value.ToString() : "-")}
Content:
{x.Content}"
            ));
    }

    private static string ResolveRecordType(RetrievedChunk chunk)
    {
        return string.IsNullOrWhiteSpace(chunk.RecordType)
            ? ChunkMetadataExtractor.DetectRecordType(chunk.Content)
            : chunk.RecordType;
    }

    private static string ValueOrFallback(string value, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        return string.IsNullOrWhiteSpace(fallback) ? "-" : fallback;
    }
}