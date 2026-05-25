using be_service.Models;
using System.Text.RegularExpressions;

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
- Jangan menambahkan informasi di luar CONTEXT.
- Jika informasi tidak ada di context, katakan:
  'Maaf, saya tidak menemukan informasi tersebut.'
- Jangan menulis pembuka seperti ""Berikut adalah jawaban berdasarkan context"".
- Jangan menambahkan penutup seperti ""Jika ada pertanyaan lain"".
- Jangan mengulang poin yang sama dengan kata-kata berbeda.
- Untuk pertanyaan semantic/rangkuman/risiko/prosedur, jawab maksimal 4-6 bullet.
- Jika CONTEXT berisi SOP dan audit, gabungkan menjadi jawaban terstruktur yang ringkas; jangan pisahkan menjadi narasi berulang.
- Jangan mengubah nama unit, NIK, kode, angka, tanggal, status, atau nilai yang ada di context.
- Jangan membahas bahwa dokumen adalah dummy, fiktif, simulasi, AI, NLP, chatbot, atau enterprise system kecuali user secara eksplisit bertanya tentang sifat dokumen.
- Jika CONTEXT berisi disclaimer seperti data dummy/fiktif, simulasi sistem AI, pengembangan chatbot, atau data contoh, abaikan bagian itu kecuali user bertanya apakah data asli.
- Jangan memberi saran eksternal seperti ""konsultasikan ke divisi"" kecuali tertulis di CONTEXT.
- Jangan membuat istilah baru.
- Jangan menerjemahkan singkatan teknis yang sudah ada. Contoh: APD tetap APD.
- Jangan menyimpulkan hal yang tidak tertulis.
- Setiap blok context adalah record yang berbeda. Jangan menggabungkan beberapa record menjadi satu record.
- Jika ditemukan lebih dari satu orang atau lebih dari satu record dengan nama yang sama, tampilkan semuanya.
- Jangan menggabungkan Data Karyawan, Rekap Lembur, SOP, dan Log Maintenance menjadi satu record.
- Kelompokkan jawaban berdasarkan RecordType jika context berisi employee, overtime, maintenance, sop, profile, audit, atau document.
- Jika context berisi Data Karyawan, tampilkan semua field: NIK, Nama, Divisi, Jabatan, Shift, Status.
- Jika context berisi Rekap Lembur, tampilkan semua field: Tanggal, Nama, Divisi, Durasi, Approval.
- Jika context berisi Log Maintenance, tampilkan semua field: Kode, Peralatan, Lokasi, Status, Teknisi.
- Jika context berisi SOP, tampilkan poin-poin SOP yang tersedia.
- Jangan menyimpulkan bahwa seseorang boleh mengakses area tertentu hanya karena ada aturan safety briefing. Jika context menyebut akses hanya untuk pihak tertentu, pihak lain dianggap tidak disebut atau tidak diperbolehkan berdasarkan context.
- Jika context berisi audit, jawab berdasarkan catatan audit yang tersedia.
- Jika user menanyakan NIK seseorang, cari field NIK pada context.
- Untuk pertanyaan exact seperti NIK, kode maintenance, dan tanggal, jawab hanya dari record yang ada di context.
- Jangan menambahkan informasi yang tidak tertulis di context.
- Untuk pertanyaan risiko, keselamatan, pengawasan, operasional, prosedur, atau insiden, prioritaskan poin yang relevan: APD, larangan perangkat elektronik non-sertifikasi, pemeriksaan kartu akses, safety briefing shift malam, akses terbatas area tangki, batas kecepatan kendaraan, simulasi evakuasi kebakaran, kepatuhan APD, pelanggaran minor, dan CCTV thermal jika tersedia di CONTEXT.
- Jawab singkat, jelas, profesional, dan langsung ke pertanyaan user.
- Jika harus memakai bullet, gunakan format ""- "" dan jangan lebih dari 6 bullet.

=====================
CONTEXT:
{context}
=====================

PERTANYAAN:
{question}

JAWABAN:
";
    }

    public string BuildPolicyGroundedPrompt(string question, List<RetrievedChunk> chunks)
    {
        var context = BuildContext(chunks);

        return $@"
Kamu menjawab pertanyaan kebijakan/aturan internal perusahaan.

ATURAN:
- Jawab hanya berdasarkan CONTEXT.
- Jangan membuat informasi, izin, pengecualian, atau aturan baru.
- Jangan menulis pembuka seperti ""Berikut adalah jawaban berdasarkan context"".
- Jangan menambahkan penutup seperti ""Jika ada pertanyaan lain"".
- Jangan mengulang poin yang sama.
- Gunakan jawaban singkat; jika perlu bullet, maksimal 4-6 poin.
- Jangan menyebut disclaimer dummy/fiktif/simulasi kecuali user bertanya apakah data asli.
- Jangan membuat istilah baru.
- Jangan menerjemahkan singkatan teknis yang sudah ada. Contoh: APD tetap APD.
- Jika CONTEXT menggunakan kata ""hanya"", pihak yang tidak disebut dalam daftar tersebut dianggap tidak diperbolehkan berdasarkan context.
- Jika CONTEXT menyebut area hanya dapat diakses oleh HSSE dan Maintenance, maka pihak selain HSSE dan Maintenance tidak boleh disimpulkan boleh.
- Jika user bertanya ""selain X"", jangan jawab tentang X sebagai pihak yang boleh.
- Jangan menyimpulkan safety briefing sebagai izin akses.
- Jangan menyimpulkan penggunaan APD sebagai izin akses.
- Jika informasi tidak cukup, jawab:
  ""Maaf, saya tidak menemukan informasi tersebut.""
- Jawab singkat, jelas, profesional, dalam Bahasa Indonesia.

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
{CleanContextForLlm(x.Content)}"
            ));
    }

    private static string CleanContextForLlm(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "";
        }

        var cleanedSegments = SplitContextSegments(content)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !IsNoiseDisclaimer(x))
            .ToList();

        if (!cleanedSegments.Any())
        {
            return content;
        }

        return string.Join("\n", cleanedSegments);
    }

    private static IEnumerable<string> SplitContextSegments(string content)
    {
        return Regex.Split(
            content,
            @"(?<=[.!?])\s+|\r?\n+");
    }

    private static bool IsNoiseDisclaimer(string value)
    {
        return ContainsAny(
            value,
            "dummy",
            "fiktif",
            "simulasi sistem",
            "tidak merepresentasikan data asli",
            "data contoh",
            "pengembangan chatbot enterprise");
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

    private static bool ContainsAny(string value, params string[] keywords)
    {
        return keywords.Any(keyword =>
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
