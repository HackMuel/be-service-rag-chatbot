using System.Text.RegularExpressions;
using be_service.Models;

namespace be_service.Services;

public class QueryAnalyzerService
{
    public RagQueryAnalysis Analyze(string question)
    {
        question = (question ?? "").Trim();

        var nikMatch = Regex.Match(
            question,
            @"\bRU\s*6\s*-?\s*\d{4}\b",
            RegexOptions.IgnoreCase
        );

        var maintenanceMatch = Regex.Match(
            question,
            @"\bMT\s*-?\s*\d{3}\b",
            RegexOptions.IgnoreCase
        );

        var dateMatch = Regex.Match(
            question,
            @"\b\d{2}-\d{2}-\d{4}\b",
            RegexOptions.IgnoreCase
        );

        var personKeyword = ExtractPersonKeyword(question);

        return new RagQueryAnalysis
        {
            Question = question,

            Nik = nikMatch.Success
                ? ChunkMetadataExtractor.NormalizeNik(nikMatch.Value)
                : string.Empty,

            MaintenanceCode = maintenanceMatch.Success
                ? ChunkMetadataExtractor.NormalizeMaintenanceCode(maintenanceMatch.Value)
                : string.Empty,

            Date = dateMatch.Success ? dateMatch.Value : string.Empty,

            IsSopQuery = IsSopQuery(question),
            IsProfileQuery = IsProfileQuery(question),
            IsAuditQuery = IsAuditQuery(question),
            IsEmployeeQuery = IsEmployeeQuery(question),
            IsOvertimeQuery = IsOvertimeQuery(question),
            IsMaintenanceQuery = IsMaintenanceQuery(question),

            SopKeyword = BuildSopKeyword(question),
            ProfileKeyword = BuildProfileKeyword(question),

            Division = ExtractDivisionFromQuestion(question),
            Shift = ExtractShiftFromQuestion(question),
            EmployeeStatus = ExtractEmployeeStatusFromQuestion(question),
            Position = ExtractPositionFromQuestion(question),

            MaintenanceStatus = ExtractMaintenanceStatusFromQuestion(question),
            Approval = ExtractApprovalFromQuestion(question),
            Location = ExtractLocationFromQuestion(question),
            Technician = ExtractTechnicianFromQuestion(question),

            PersonKeyword = personKeyword,
            LooksLikePersonName = LooksLikePersonName(personKeyword)
        };
    }

    private static bool IsSopQuery(string question)
    {
        return ContainsAny(
            question,
            "sop",
            "keamanan",
            "apd",
            "area kilang",
            "area produksi",
            "tangki",
            "penyimpanan");
    }

    private static bool IsProfileQuery(string question)
    {
        return ContainsAny(
            question,
            "profil",
            "nama unit",
            "unit perusahaan",
            "kapasitas",
            "kapasitas produksi",
            "lokasi",
            "jumlah karyawan",
            "jumlah shift",
            "shift operasional",
            "direktur operasi",
            "perusahaan dalam dokumen");
    }

    private static bool IsAuditQuery(string question)
    {
        return ContainsAny(
            question,
            "audit",
            "backup",
            "server",
            "logbook",
            "anomali suhu",
            "kepatuhan apd",
            "pelanggaran minor");
    }

    private static bool IsOvertimeQuery(string question)
    {
        return ContainsAny(question, "lembur", "rekap lembur");
    }

    private static bool IsEmployeeQuery(string question)
    {
        return ContainsAny(question, "karyawan", "pegawai");
    }

    private static bool IsMaintenanceQuery(string question)
    {
        return ContainsAny(
            question,
            "maintenance",
            "peralatan",
            "teknisi",
            "log maintenance");
    }

    private static string ExtractDivisionFromQuestion(string question)
    {
        if (ContainsAny(question, "it & digitalisasi", "it digitalisasi", "digitalisasi"))
            return "IT & Digitalisasi";

        if (ContainsAny(question, "operasional kilang"))
            return "Operasional Kilang";

        if (ContainsAny(question, "human capital"))
            return "Human Capital";

        if (ContainsAny(question, "maintenance"))
            return "Maintenance";

        if (ContainsAny(question, "distribusi"))
            return "Distribusi";

        if (ContainsAny(question, "keuangan"))
            return "Keuangan";

        if (ContainsAny(question, "security"))
            return "Security";

        if (ContainsAny(question, "hsse"))
            return "HSSE";

        return string.Empty;
    }

    private static string ExtractShiftFromQuestion(string question)
    {
        var match = Regex.Match(
            question,
            @"\bshift\s*([ABC])\b",
            RegexOptions.IgnoreCase
        );

        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
    }

    private static string ExtractEmployeeStatusFromQuestion(string question)
    {
        if (ContainsAny(question, "kontrak"))
            return "Kontrak";

        if (ContainsAny(question, "tetap"))
            return "Tetap";

        return string.Empty;
    }

    private static string ExtractPositionFromQuestion(string question)
    {
        var positions = new[]
        {
            "Supervisor",
            "Operator",
            "Manager",
            "Coordinator",
            "Analyst",
            "Engineer",
            "Staff"
        };

        return ExtractCanonicalValue(question, positions);
    }

    private static string ExtractMaintenanceStatusFromQuestion(string question)
    {
        if (ContainsAny(question, "maintenance berkala", "berkala"))
            return "Maintenance Berkala";

        if (ContainsAny(question, "perbaikan"))
            return "Perbaikan";

        if (Regex.IsMatch(question, @"\bnormal\b", RegexOptions.IgnoreCase))
            return "Normal";

        return string.Empty;
    }

    private static string ExtractApprovalFromQuestion(string question)
    {
        if (ContainsAny(question, "pending"))
            return "Pending";

        if (ContainsAny(question, "disetujui"))
            return "Disetujui";

        if (ContainsAny(question, "ditolak"))
            return "Ditolak";

        return string.Empty;
    }

    private static string ExtractLocationFromQuestion(string question)
    {
        var locations = new[]
        {
            "Gate Utama",
            "Utility Plant",
            "Area Produksi B",
            "Area Tanki A",
            "Warehouse"
        };

        return ExtractCanonicalValue(question, locations);
    }

    private static string ExtractTechnicianFromQuestion(string question)
    {
        var cleaned = Regex.Replace(question, @"[?.,!]", " ");

        var stopWords = new[]
        {
            "siapa",
            "saja",
            "teknisi",
            "maintenance",
            "peralatan",
            "log",
            "tampilkan",
            "berikan",
            "data",
            "di",
            "lokasi",
            "yang",
            "bernama",
            "dengan",
            "nama"
        };

        foreach (var stopWord in stopWords)
        {
            cleaned = Regex.Replace(
                cleaned,
                $@"\b{Regex.Escape(stopWord)}\b",
                " ",
                RegexOptions.IgnoreCase);
        }

        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        return LooksLikePersonName(cleaned) ? cleaned : string.Empty;
    }

    private static string ExtractPersonKeyword(string question)
    {
        var cleaned = Regex.Replace(question, @"[?.,!]", " ");

        var stopWords = new[]
        {
            "apa",
            "itu",
            "siapa",
            "saja",
            "semua",
            "berikan",
            "beri",
            "tampilkan",
            "data",
            "karyawan",
            "pegawai",
            "dengan",
            "nama",
            "nik",
            "berapa",
            "nya",
            "yang",
            "memiliki",
            "punya",
            "tolong",
            "carikan",
            "info",
            "informasi",
            "tentang",
            "dari",
            "pada",
            "untuk",
            "rekap",
            "lembur",
            "maintenance",
            "log",
            "kode"
        };

        foreach (var stopWord in stopWords)
        {
            cleaned = Regex.Replace(
                cleaned,
                $@"\b{Regex.Escape(stopWord)}\b",
                " ",
                RegexOptions.IgnoreCase);
        }

        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    private static bool LooksLikePersonName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var lowerValue = value.ToLowerInvariant();

        var domainWords = new[]
        {
            "sop",
            "apd",
            "aturan",
            "area",
            "produksi",
            "tangki",
            "penyimpanan",
            "akses",
            "kilang",
            "maintenance",
            "lembur",
            "rekap",
            "log",
            "tanggal",
            "status",
            "peralatan",
            "teknisi",
            "kode",
            "ru6",
            "mt"
        };

        if (domainWords.Any(x => lowerValue.Contains(x)))
            return false;

        if (Regex.IsMatch(value, @"\d"))
            return false;

        var words = Regex.Matches(value, @"[A-Za-z]+")
            .Select(x => x.Value)
            .ToList();

        return words.Count is >= 2 and <= 4 &&
               words.All(x => x.Length >= 2);
    }

    private static string BuildSopKeyword(string question)
    {
        if (ContainsAny(question, "tangki", "penyimpanan"))
            return "tangki penyimpanan";

        if (ContainsAny(question, "apd"))
            return "APD";

        if (ContainsAny(question, "area produksi"))
            return "area produksi";

        if (ContainsAny(question, "akses", "mengakses"))
            return "akses";

        if (ContainsAny(question, "sop", "keamanan", "area kilang"))
            return "SOP Keamanan Area Kilang";

        return question;
    }

    private static string BuildProfileKeyword(string question)
    {
        if (ContainsAny(question, "nama unit", "unit perusahaan"))
            return "Nama Unit";

        if (ContainsAny(question, "kapasitas", "kapasitas produksi"))
            return "Kapasitas Produksi";

        if (ContainsAny(question, "lokasi", "alamat"))
            return "Lokasi";

        if (ContainsAny(question, "jumlah karyawan"))
            return "Jumlah Karyawan";

        if (ContainsAny(question, "jumlah shift", "shift operasional"))
            return "Jumlah Shift Operasional";

        if (ContainsAny(question, "direktur operasi"))
            return "Direktur Operasi";

        return string.Empty;
    }

    private static string ExtractCanonicalValue(string question, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (Regex.IsMatch(
                    question,
                    $@"\b{Regex.Escape(value)}\b",
                    RegexOptions.IgnoreCase))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static bool ContainsAny(string value, params string[] keywords)
    {
        return keywords.Any(keyword =>
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}