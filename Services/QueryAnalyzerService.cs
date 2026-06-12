using System.Text.RegularExpressions;
using be_service.Models;

namespace be_service.Services;

public class QueryAnalyzerService
{
    private record AnswerLevelContext(
        string Question,
        bool HasNik,
        bool HasMaintenanceCode,
        bool HasDate,
        bool IsEmployeeQuery,
        bool IsOvertimeQuery,
        bool IsMaintenanceQuery,
        bool IsSopQuery,
        bool IsProfileQuery,
        bool IsAuditQuery,
        bool HasDivision,
        bool HasShift,
        bool HasEmployeeStatus,
        bool HasPosition,
        bool HasMaintenanceStatus,
        bool HasApproval,
        bool HasLocation,
        bool HasTechnician,
        bool LooksLikePersonName,
        bool IsPolicyQuestion,
        string GenericRecordType);

    private readonly FieldIntentClassifier _fieldIntentClassifier;

    public QueryAnalyzerService(FieldIntentClassifier fieldIntentClassifier)
    {
        _fieldIntentClassifier = fieldIntentClassifier;
    }

    public async Task<RagQueryAnalysis> AnalyzeAsync(string question)
    {
        question = (question ?? "").Trim();

        var keywordFields = ExtractRequestedFields(question);

        // Only invoke LLM when keyword scan found nothing AND query has heuristic
        // indicators of a synonym/informal sensitive field request. This keeps the
        // fast path (keyword hit or no suspicious pattern) at ~1ms instead of ~6s.
        var llmFields = new List<string>();
        if (keywordFields.Count == 0 && MightHaveUnknownFieldIntent(question))
        {
            var llmIntent = await _fieldIntentClassifier.ClassifyAsync(question);
            llmFields = llmIntent.RequestedFields;
        }

        var requestedFields = keywordFields.Union(llmFields).Distinct().ToList();
        var fieldValidation = ValidateFields(requestedFields);

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
        var looksLikePersonName = LooksLikePersonName(personKeyword);

        var isSopQuery = IsSopQuery(question);
        var isProfileQuery = IsProfileQuery(question);
        var isAuditQuery = IsAuditQuery(question);
        var isEmployeeQuery = IsEmployeeQuery(question);
        var isOvertimeQuery = IsOvertimeQuery(question);
        var isMaintenanceQuery = IsMaintenanceQuery(question);

        var division = ExtractDivisionFromQuestion(question);
        var shift = ExtractShiftFromQuestion(question);
        var employeeStatus = ExtractEmployeeStatusFromQuestion(question);
        var position = ExtractPositionFromQuestion(question);
        var maintenanceStatus = ExtractMaintenanceStatusFromQuestion(question);
        var approval = ExtractApprovalFromQuestion(question);
        var location = ExtractLocationFromQuestion(question);
        var technician = ExtractTechnicianFromQuestion(question);

        var isAccessQuestion = IsAccessQuestion(question);
        var isPermissionQuestion = IsPermissionQuestion(question);
        var isPolicyQuestion = IsPolicyQuestion(question);
        var targetRecordType = DetermineTargetRecordType(
            question,
            isSopQuery,
            isAuditQuery,
            isProfileQuery);
        var genericRecordType = DetectGenericRecordType(
            question,
            nikMatch.Success,
            maintenanceMatch.Success,
            dateMatch.Success,
            looksLikePersonName,
            isPolicyQuestion,
            !string.IsNullOrWhiteSpace(division),
            !string.IsNullOrWhiteSpace(shift),
            !string.IsNullOrWhiteSpace(employeeStatus),
            !string.IsNullOrWhiteSpace(position),
            !string.IsNullOrWhiteSpace(maintenanceStatus),
            !string.IsNullOrWhiteSpace(approval),
            !string.IsNullOrWhiteSpace(location),
            !string.IsNullOrWhiteSpace(technician));

        var ctx = new AnswerLevelContext(
            question,
            nikMatch.Success,
            maintenanceMatch.Success,
            dateMatch.Success,
            isEmployeeQuery,
            isOvertimeQuery,
            isMaintenanceQuery,
            isSopQuery,
            isProfileQuery,
            isAuditQuery,
            !string.IsNullOrWhiteSpace(division),
            !string.IsNullOrWhiteSpace(shift),
            !string.IsNullOrWhiteSpace(employeeStatus),
            !string.IsNullOrWhiteSpace(position),
            !string.IsNullOrWhiteSpace(maintenanceStatus),
            !string.IsNullOrWhiteSpace(approval),
            !string.IsNullOrWhiteSpace(location),
            !string.IsNullOrWhiteSpace(technician),
            looksLikePersonName,
            isPolicyQuestion,
            genericRecordType);
        var answerLevel = DetermineAnswerLevel(ctx);

        if (fieldValidation.IsBlocked)
            answerLevel = AnswerLevel.Blocked;

        return new RagQueryAnalysis
        {
            Question = question,
            AnswerLevel = answerLevel,

            Nik = nikMatch.Success
                ? ChunkMetadataExtractor.NormalizeNik(nikMatch.Value)
                : string.Empty,

            MaintenanceCode = maintenanceMatch.Success
                ? ChunkMetadataExtractor.NormalizeMaintenanceCode(maintenanceMatch.Value)
                : string.Empty,

            Date = dateMatch.Success ? dateMatch.Value : string.Empty,

            IsSopQuery = isSopQuery,
            IsProfileQuery = isProfileQuery,
            IsAuditQuery = isAuditQuery,
            IsEmployeeQuery = isEmployeeQuery,
            IsOvertimeQuery = isOvertimeQuery,
            IsMaintenanceQuery = isMaintenanceQuery,

            SopKeyword = BuildSopKeyword(question),
            ProfileKeyword = BuildProfileKeyword(question),

            Division = division,
            Shift = shift,
            EmployeeStatus = employeeStatus,
            Position = position,

            MaintenanceStatus = maintenanceStatus,
            Approval = approval,
            Location = location,
            Technician = technician,

            PersonKeyword = personKeyword,
            LooksLikePersonName = looksLikePersonName,

            IsPolicyQuestion = isPolicyQuestion,
            IsAccessQuestion = isAccessQuestion,
            IsPermissionQuestion = isPermissionQuestion,
            RequiresGroundedLlm = answerLevel is AnswerLevel.PolicyGrounded or AnswerLevel.SemanticGrounded,
            TargetRecordType = targetRecordType,
            GenericRecordType = genericRecordType,
            RequestedFields = requestedFields,
            IsBlocked = fieldValidation.IsBlocked,
            BlockReason = fieldValidation.BlockReason
        };
    }

    private static bool IsSopQuery(string question)
    {
        return ContainsAny(
            question,
            "sop",
            "keamanan",
            "maks",
            "maksimal",
            "apd",
            "area kilang",
            "area produksi",
            "tangki",
            "penyimpanan",
            "kecepatan",
            "kendaraan",
            "km/jam",
            "perangkat elektronik",
            "non-sertifikasi",
            "rawan ledakan",
            "safety briefing",
            "shift malam");
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
            "kepatuhan",
            "inspeksi",
            "temuan",
            "non-konformitas",
            "logbook",
            "anomali suhu",
            "kepatuhan apd",
            "pelanggaran minor");
    }

    private static bool IsItInfraQuery(string question) =>
        ContainsAny(question, "server", "backup", "cctv",
            "jaringan", "infrastruktur it", "sistem it", "database server");

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

    private static bool IsAccessQuestion(string question)
    {
        return ContainsAny(
            question,
            "akses",
            "mengakses",
            "masuk",
            "area tangki",
            "area penyimpanan");
    }

    private static bool IsPermissionQuestion(string question)
    {
        return ContainsAny(
            question,
            "boleh",
            "bolehkah",
            "diperbolehkan",
            "izin",
            "apakah orang",
            "apakah personel",
            "apakah pekerja");
    }

    private static bool IsPolicyQuestion(string question)
    {
        return IsAccessQuestion(question) ||
               IsPermissionQuestion(question) ||
               ContainsAny(
                   question,
                   "selain",
                   "kecuali",
                   "bukan",
                   "non-hsse",
                   "non hsse",
                   "divisi lain",
                   "pekerja dari divisi lain");
    }

    private static string DetermineTargetRecordType(
        string question,
        bool isSopQuery,
        bool isAuditQuery,
        bool isProfileQuery)
    {
        if (isProfileQuery)
            return "profile";

        if (isAuditQuery || IsItInfraQuery(question))
        {
            return "audit";
        }

        if (isSopQuery)
            return "sop";

        return string.Empty;
    }

    private static AnswerLevel DetermineAnswerLevel(AnswerLevelContext ctx)
    {
        var (question, hasNik, hasMaintenanceCode, hasDate,
             isEmployeeQuery, isOvertimeQuery, isMaintenanceQuery,
             isSopQuery, isProfileQuery, isAuditQuery,
             hasDivision, hasShift, hasEmployeeStatus, hasPosition,
             hasMaintenanceStatus, hasApproval, hasLocation, hasTechnician,
             looksLikePersonName, isPolicyQuestion, genericRecordType) = ctx;

        var looksLikeStructuredName =
            looksLikePersonName &&
            !isSopQuery &&
            !isProfileQuery &&
            !isAuditQuery;

        if (!string.IsNullOrWhiteSpace(genericRecordType))
        {
            return genericRecordType is "sop" or "audit"
                ? AnswerLevel.DeterministicTemplate
                : AnswerLevel.ExactStructured;
        }

        if (hasNik || hasMaintenanceCode || hasDate ||
            (isEmployeeQuery && (hasDivision || hasShift || hasEmployeeStatus || hasPosition)) ||
            (isOvertimeQuery && (hasApproval || hasDivision || looksLikeStructuredName)) ||
            (isMaintenanceQuery && (hasMaintenanceStatus || hasLocation || hasTechnician)) ||
            looksLikeStructuredName)
        {
            return AnswerLevel.ExactStructured;
        }

        if (IsDirectAccessListQuestion(question) && isSopQuery)
        {
            return AnswerLevel.DeterministicTemplate;
        }

        if (isPolicyQuestion)
        {
            return AnswerLevel.PolicyGrounded;
        }

        if (IsSemanticGroundedQuestion(question))
        {
            return AnswerLevel.SemanticGrounded;
        }

        if (IsDeterministicTemplateQuestion(question, isSopQuery, isProfileQuery, isAuditQuery))
        {
            return AnswerLevel.DeterministicTemplate;
        }

        return AnswerLevel.SemanticGrounded;
    }

    private static bool IsDeterministicTemplateQuestion(
        string question,
        bool isSopQuery,
        bool isProfileQuery,
        bool isAuditQuery)
    {
        if (isProfileQuery)
            return true;

        if (IsItInfraQuery(question))
            return true;

        if (isAuditQuery && ContainsAny(
                question,
                "kepatuhan",
                "pelanggaran minor",
                "anomali suhu"))
        {
            return true;
        }

        if (!isSopQuery)
            return false;

        return ContainsAny(
            question,
            "apa saja sop",
            "sebutkan sop",
            "daftar sop",
            "kecepatan",
            "kendaraan",
            "km/jam",
            "apd wajib",
            "aturan apd",
            "perangkat elektronik",
            "non-sertifikasi",
            "rawan ledakan",
            "safety briefing",
            "shift malam");
    }

    private static bool IsSemanticGroundedQuestion(string question)
    {
        return ContainsAny(
            question,
            "ringkas",
            "jelaskan",
            "bagaimana",
            "risiko",
            "prosedur",
            "evaluasi",
            "gambaran umum");
    }

    private static bool IsDirectAccessListQuestion(string question)
    {
        return ContainsAny(question, "siapa saja yang boleh", "siapa yang boleh") &&
               ContainsAny(question, "akses", "mengakses", "masuk", "tangki", "penyimpanan");
    }

    private static string ExtractDivisionFromQuestion(string question)
    {
        if (ContainsAny(question, "it & digitalisasi", "it digitalisasi", "digitalisasi") ||
            Regex.IsMatch(question, @"\bIT\b", RegexOptions.IgnoreCase))
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
            "apakah",
            "siapa",
            "saja",
            "semua",
            "saya",
            "aku",
            "kamu",
            "ada",
            "berikan",
            "beri",
            "tampilkan",
            "cari",
            "data",
            "karyawan",
            "pegawai",
            "dengan",
            "nama",
            "bernama",
            "atas",
            "nik",
            "berapa",
            "nya",
            "yang",
            "memiliki",
            "punya",
            "tolong",
            "mohon",
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
            "kode",
            "gaji",
            "penghasilan",
            "upah",
            "tunjangan",
            "honor",
            "bulan",
            "tahun",
            "hari",
            "kapan",
            "jadwal",
            "divisi",
            "server",
            "backup",
            "password",
            "cctv"
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

    private static string DetectGenericRecordType(
        string question,
        bool hasNik,
        bool hasMaintenanceCode,
        bool hasDate,
        bool looksLikePersonName,
        bool isPolicyQuestion,
        bool hasDivision,
        bool hasShift,
        bool hasEmployeeStatus,
        bool hasPosition,
        bool hasMaintenanceStatus,
        bool hasApproval,
        bool hasLocation,
        bool hasTechnician)
    {
        if (hasNik ||
            hasMaintenanceCode ||
            hasDate ||
            looksLikePersonName ||
            isPolicyQuestion)
        {
            return string.Empty;
        }

        if (!IsGenericDataQuestion(question))
            return string.Empty;

        if (ContainsAny(question, "karyawan", "pegawai"))
            return "employee";

        if (ContainsAny(question, "lembur", "rekap lembur"))
            return "overtime";

        if (ContainsAny(question, "maintenance", "log maintenance", "perawatan"))
            return "maintenance";

        if (ContainsAny(question, "sop"))
            return "sop";

        if (ContainsAny(question, "audit"))
            return "audit";

        return string.Empty;
    }

    private static bool IsGenericDataQuestion(string question)
    {
        return ContainsAny(
            question,
            "data karyawan",
            "data pegawai",
            "data lembur",
            "rekap lembur",
            "data maintenance",
            "data perawatan",
            "ada data",
            "apakah ada",
            "berikan data",
            "berikan saya data",
            "tampilkan data",
            "tolong tampilkan data",
            "memiliki data",
            "punya data",
            "data tentang",
            "ada log",
            "log maintenance",
            "dokumen ini punya",
            "punya sop",
            "sop apa saja",
            "apa saja sop");
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

        if (ContainsAny(question, "kecepatan", "kendaraan", "km/jam"))
            return "kendaraan 20 km/jam area produksi";

        if (ContainsAny(question, "perangkat elektronik", "non-sertifikasi", "rawan ledakan"))
            return "perangkat elektronik";

        if (ContainsAny(question, "safety briefing", "shift malam"))
            return "safety briefing";

        if (ContainsAny(question, "akses", "mengakses"))
            return "akses";

        if (ContainsAny(question, "area produksi"))
            return "area produksi";

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

    // Returns true only for queries that look like they might reference a
    // sensitive field via informal phrasing or synonyms not covered by keyword map.
    // Keep this list narrow — false positives cost a full LLM round-trip (~5s).
    private static bool MightHaveUnknownFieldIntent(string question) =>
        ContainsAny(question,
            "dapatnya", "kompensasi", "remunerasi", "sandi", "login credential",
            "kasih tau", "tolong kasih", "ceritakan", "berapa besar", "berapa total");

    private static List<string> ExtractRequestedFields(string question) =>
        FieldKeywordMap.ExtractFieldKeys(question);

    private static FieldValidationResult ValidateFields(List<string> fields)
    {
        foreach (var fieldKey in fields)
        {
            var access = FieldSchema.GetAccess(fieldKey);

            if (access == FieldAccess.Sensitive)
                return FieldValidationResult.Block(
                    $"Maaf, informasi tentang '{fieldKey.Replace('_', ' ')}' bersifat sensitif dan tidak dapat diakses melalui sistem ini.");

            if (access == FieldAccess.Unavailable)
                return FieldValidationResult.Block(
                    $"Maaf, informasi tentang '{fieldKey.Replace('_', ' ')}' tidak tersedia dalam sistem ini.");
        }

        return FieldValidationResult.Pass();
    }

    private static bool ContainsAny(string value, params string[] keywords)
    {
        return keywords.Any(keyword =>
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
