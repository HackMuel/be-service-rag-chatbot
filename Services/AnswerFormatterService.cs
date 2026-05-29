using be_service.Models;
using System.Text.RegularExpressions;

namespace be_service.Services;

public class AnswerFormatterService
{
    public string? TryBuildDeterministicAnswer(
        List<RetrievedChunk> chunks,
        string retrievalMode,
        string question)
    {
        var operationalRiskAnswer = TryBuildOperationalRiskAnswer(chunks, question);

        if (!string.IsNullOrWhiteSpace(operationalRiskAnswer))
        {
            return operationalRiskAnswer;
        }

        if (retrievalMode == "profile")
        {
            return BuildProfileAnswer(chunks, question);
        }

        if (retrievalMode is "audit" or "audit_general")
        {
            return BuildAuditAnswer(chunks, question) ??
                   (retrievalMode == "audit_general" ? BuildNarrativeRecordTypeAnswer("Data Audit", chunks) : null);
        }

        if (retrievalMode is "sop" or "sop_general")
        {
            return BuildSopAnswer(chunks, question) ??
                   (retrievalMode == "sop_general"
                       ? BuildSopListAnswer(string.Join("\n", chunks.Select(x => x.Content)))
                       : null);
        }

        if (retrievalMode == "exact-maintenance-code")
        {
            var answer = BuildMaintenanceFieldSpecificAnswer(chunks.First(), question);

            if (!string.IsNullOrWhiteSpace(answer))
                return answer;
        }

        if (retrievalMode == "semantic")
        {
            return null;
        }

        return chunks.Any(IsStructuredRecord)
            ? BuildStructuredAnswer(chunks)
            : null;
    }

    public string? TryBuildOperationalRiskAnswer(
        List<RetrievedChunk> chunks,
        string question)
    {
        if (!IsOperationalRiskQuestion(question))
        {
            return null;
        }

        var relevantContent = chunks
            .Where(chunk => ResolveRecordType(chunk) is "sop" or "audit")
            .SelectMany(chunk => ExtractSentences(chunk.Content))
            .Where(sentence => !IsNoiseSentence(sentence))
            .ToList();

        if (!relevantContent.Any())
        {
            return null;
        }

        var content = string.Join("\n", relevantContent);
        var bullets = new List<string>();

        if (ContainsAny(content, "APD lengkap", "menggunakan APD") &&
            ContainsAny(content, "area produksi"))
        {
            bullets.Add("Seluruh pekerja wajib menggunakan APD lengkap di area produksi.");
        }

        if (ContainsAny(content, "perangkat elektronik") &&
            ContainsAny(content, "non-sertifikasi", "rawan ledakan"))
        {
            bullets.Add("Perangkat elektronik non-sertifikasi dilarang di area rawan ledakan.");
        }

        var accessAndBriefing = new List<string>();

        if (ContainsAny(content, "kartu akses") &&
            ContainsAny(content, "gate utama"))
        {
            accessAndBriefing.Add("Pemeriksaan kartu akses dilakukan di seluruh gate utama");
        }

        if (ContainsAny(content, "safety briefing", "shift malam"))
        {
            var briefingSentence = FindFirstMatchingSentence(
                relevantContent,
                "safety briefing",
                "shift malam");
            var briefingTime = ExtractTime(briefingSentence);
            accessAndBriefing.Add(string.IsNullOrWhiteSpace(briefingTime)
                ? "safety briefing shift malam wajib dilakukan"
                : $"safety briefing shift malam dilakukan pukul {briefingTime}");
        }

        if (accessAndBriefing.Any())
        {
            bullets.Add($"{string.Join(" dan ", accessAndBriefing)}.");
        }

        var accessAndSpeed = new List<string>();

        if (ContainsAny(content, "tangki penyimpanan", "area tangki") &&
            ContainsAny(content, "HSSE") &&
            ContainsAny(content, "Maintenance"))
        {
            accessAndSpeed.Add("Area tangki penyimpanan hanya dapat diakses personel HSSE dan Maintenance");
        }

        var speed = ExtractSpeed(content);

        if (!string.IsNullOrWhiteSpace(speed))
        {
            accessAndSpeed.Add($"kendaraan operasional maksimal {speed} di area produksi");
        }

        if (accessAndSpeed.Any())
        {
            bullets.Add($"{string.Join(", serta ", accessAndSpeed)}.");
        }

        if (ContainsAny(content, "evakuasi kebakaran") &&
            ContainsAny(content, "3 bulan", "tiga bulan"))
        {
            bullets.Add("Simulasi evakuasi kebakaran dilakukan setiap 3 bulan.");
        }

        var auditFindings = new List<string>();

        if (ContainsAny(content, "pelanggaran minor") &&
            ContainsAny(content, "logbook"))
        {
            var violationCount = ExtractCountBeforePhrase(content, "pelanggaran minor");
            auditFindings.Add(string.IsNullOrWhiteSpace(violationCount)
                ? "audit mencatat pelanggaran minor terkait keterlambatan pengisian logbook digital pada shift malam"
                : $"audit mencatat {violationCount} pelanggaran minor terkait keterlambatan pengisian logbook digital pada shift malam");
        }

        if (ContainsAny(content, "CCTV thermal", "anomali suhu"))
        {
            var anomalyCount = ExtractCountBeforePhrase(content, "anomali suhu");
            auditFindings.Add(string.IsNullOrWhiteSpace(anomalyCount)
                ? "CCTV thermal mendeteksi anomali suhu pada area penyimpanan bahan bakar"
                : $"CCTV thermal mendeteksi {anomalyCount} anomali suhu pada area penyimpanan bahan bakar");
        }

        if (ContainsAny(content, "backup", "server internal"))
        {
            var backupSentence = FindFirstMatchingSentence(
                relevantContent,
                "backup",
                "server internal");
            var backupTime = ExtractTime(backupSentence);
            auditFindings.Add(string.IsNullOrWhiteSpace(backupTime)
                ? "backup server internal dilakukan otomatis"
                : $"backup server internal dilakukan otomatis pukul {backupTime}");
        }

        if (auditFindings.Any())
        {
            bullets.Add($"{CapitalizeFirst(string.Join(", ", auditFindings))}.");
        }

        if (!bullets.Any())
        {
            return null;
        }

        return string.Join(
            "\n",
            bullets
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .Select(x => $"- {x}"));
    }

    private static string? BuildStructuredAnswer(List<RetrievedChunk> chunks)
    {
        var lines = new List<string>();

        var employeeChunks = chunks
            .Where(x => ResolveRecordType(x) == "employee")
            .ToList();

        if (employeeChunks.Any())
        {
            AppendEmployeeAnswer(lines, employeeChunks);
        }

        var overtimeChunks = chunks
            .Where(x => ResolveRecordType(x) == "overtime")
            .ToList();

        if (overtimeChunks.Any())
        {
            if (lines.Any())
                lines.Add("");

            AppendOvertimeAnswer(lines, overtimeChunks);
        }

        var maintenanceChunks = chunks
            .Where(x => ResolveRecordType(x) == "maintenance")
            .ToList();

        if (maintenanceChunks.Any())
        {
            if (lines.Any())
                lines.Add("");

            AppendMaintenanceAnswer(lines, maintenanceChunks);
        }

        return lines.Any() ? string.Join("\n", lines) : null;
    }

    private static string? BuildAuditAnswer(List<RetrievedChunk> chunks, string question)
    {
        var content = string.Join("\n", chunks.Select(x => x.Content));

        if (ContainsAny(question, "backup", "server"))
        {
            var timeMatch = Regex.Match(
                content,
                @"\b\d{1,2}[\.:]\d{2}\s*WIB\b",
                RegexOptions.IgnoreCase);

            if (timeMatch.Success)
            {
                var time = Regex
                    .Replace(timeMatch.Value.Trim(), @"\s+", " ")
                    .Replace(":", ".");

                return $"Backup otomatis server internal dilakukan pukul {time}.";
            }
        }

        if (ContainsAny(question, "kepatuhan", "apd"))
        {
            var percentMatch = Regex.Match(
                content,
                @"\b\d{1,3}(?:[,.]\d+)?\s*%",
                RegexOptions.IgnoreCase);

            if (percentMatch.Success)
            {
                var percent = Regex.Replace(percentMatch.Value.Trim(), @"\s+", "");

                return $"Tingkat kepatuhan penggunaan APD pada audit April 2026 adalah {percent}.";
            }
        }

        if (ContainsAny(question, "pelanggaran minor"))
        {
            var violationMatch = Regex.Match(
                content,
                @"\b(\d+)\s+pelanggaran\s+minor\b",
                RegexOptions.IgnoreCase);

            if (violationMatch.Success)
            {
                return $"Pada audit April 2026 ditemukan {violationMatch.Groups[1].Value} pelanggaran minor.";
            }
        }

        if (ContainsAny(question, "anomali suhu", "cctv"))
        {
            var anomalyMatch = Regex.Match(
                content,
                @"\b(\d+)\s+anomali\s+suhu\b",
                RegexOptions.IgnoreCase);

            if (anomalyMatch.Success)
            {
                return $"Sistem CCTV mendeteksi {anomalyMatch.Groups[1].Value} anomali suhu.";
            }
        }

        return null;
    }

    private static string? BuildSopAnswer(List<RetrievedChunk> chunks, string question)
    {
        if (ShouldUsePolicyGroundedForSop(question))
        {
            return null;
        }

        var content = string.Join("\n", chunks.Select(x => x.Content));

        if (ContainsAny(
                question,
                "apa saja sop",
                "sop apa saja",
                "sebutkan sop",
                "daftar sop",
                "punya sop",
                "dokumen ini punya sop"))
        {
            return BuildSopListAnswer(content);
        }

        if (ContainsAny(question, "kecepatan", "kendaraan", "km/jam"))
        {
            var speedMatch = Regex.Match(
                content,
                @"\b\d+\s*km\s*/?\s*jam\b",
                RegexOptions.IgnoreCase);

            if (speedMatch.Success)
            {
                var speed = Regex.Replace(speedMatch.Value.Trim(), @"\s+", " ");
                return $"Kecepatan maksimal kendaraan di area produksi adalah {speed}.";
            }
        }

        if (ContainsAny(question, "apd wajib", "aturan apd"))
        {
            var apdSentence = FindSentence(content, "APD");

            if (!string.IsNullOrWhiteSpace(apdSentence))
                return apdSentence;
        }

        if (ContainsAny(question, "perangkat elektronik", "non-sertifikasi", "rawan ledakan"))
        {
            var electronicSentence = FindSentence(content, "perangkat elektronik", "non-sertifikasi", "rawan ledakan");

            if (!string.IsNullOrWhiteSpace(electronicSentence))
                return electronicSentence;
        }

        if (ContainsAny(question, "safety briefing", "shift malam"))
        {
            var briefingSentence = FindSentence(content, "safety briefing", "shift malam");

            if (!string.IsNullOrWhiteSpace(briefingSentence))
                return briefingSentence;
        }

        if (!ContainsAny(question, "tangki", "penyimpanan", "area penyimpanan", "akses", "masuk"))
        {
            return null;
        }

        var sentence = FindSopAccessSentence(chunks);

        if (string.IsNullOrWhiteSpace(sentence))
        {
            return null;
        }

        var accessIsRestricted = Regex.IsMatch(
            sentence,
            @"hanya\s+dapat\s+diakses\s+(?:oleh\s+)?personel\s+HSSE\s+dan\s+Maintenance",
            RegexOptions.IgnoreCase);

        if (!accessIsRestricted)
        {
            return null;
        }

        if (Regex.IsMatch(question, @"\bIT\b", RegexOptions.IgnoreCase) ||
            ContainsAny(question, "digitalisasi"))
        {
            return "Tidak. Berdasarkan SOP, area tangki penyimpanan hanya dapat diakses oleh personel HSSE dan Maintenance. Personel IT tidak disebut sebagai pihak yang boleh mengakses area tersebut.";
        }

        if (ContainsAny(question, "HSSE"))
        {
            return "Ya. Berdasarkan SOP, personel HSSE termasuk pihak yang boleh mengakses area tangki penyimpanan.";
        }

        if (ContainsAny(question, "Maintenance"))
        {
            return "Ya. Berdasarkan SOP, personel Maintenance termasuk pihak yang boleh mengakses area tangki penyimpanan.";
        }

        return "Area tangki penyimpanan hanya dapat diakses oleh personel HSSE dan Maintenance.";
    }

    private static bool ShouldUsePolicyGroundedForSop(string question)
    {
        if (IsDirectAccessListQuestion(question))
        {
            return false;
        }

        return ContainsAny(
            question,
            "selain",
            "bukan",
            "non-hsse",
            "non hsse",
            "kecuali",
            "divisi lain",
            "apakah orang",
            "apakah pekerja",
            "boleh",
            "diperbolehkan",
            "izin",
            "akses",
            "mengakses",
            "masuk");
    }

    private static bool IsDirectAccessListQuestion(string question)
    {
        return ContainsAny(question, "siapa saja yang boleh", "siapa yang boleh") &&
               ContainsAny(question, "akses", "mengakses", "masuk", "tangki", "penyimpanan");
    }

    private static string? BuildSopListAnswer(string content)
    {
        var sentences = ExtractSentences(content)
            .Where(x => !x.Contains("SOP Keamanan Area Kilang", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!sentences.Any())
            return null;

        var lines = new List<string>
        {
            "SOP Keamanan Area Kilang:"
        };

        lines.AddRange(sentences.Select(x => $"- {x}"));

        return string.Join("\n", lines);
    }

    private static string? BuildNarrativeRecordTypeAnswer(
        string title,
        List<RetrievedChunk> chunks)
    {
        var sentences = chunks
            .SelectMany(x => ExtractSentences(x.Content))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        if (!sentences.Any())
            return null;

        var lines = new List<string> { $"{title}:" };
        lines.AddRange(sentences.Select(x => $"- {x}"));

        return string.Join("\n", lines);
    }

    private static string FindSentence(string content, params string[] keywords)
    {
        return ExtractSentences(content)
            .FirstOrDefault(sentence => ContainsAny(sentence, keywords)) ?? "";
    }

    private static List<string> ExtractSentences(string content)
    {
        return Regex
            .Split(content, @"(?<=[.!?])\s+|\r?\n+")
            .Select(x => Regex.Replace(x, @"^\s*[-\d.)]+\s*", "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static bool IsOperationalRiskQuestion(string question)
    {
        return ContainsAny(
            question,
            "pengendalian risiko",
            "risiko operasional",
            "keselamatan operasional",
            "pencegahan insiden",
            "pengawasan keselamatan");
    }

    private static bool IsNoiseSentence(string sentence)
    {
        return ContainsAny(
            sentence,
            "dummy",
            "fiktif",
            "simulasi",
            "tidak merepresentasikan data asli",
            "data contoh",
            "pengembangan chatbot enterprise");
    }

    private static string ExtractTime(string content)
    {
        var timeMatch = Regex.Match(
            content,
            @"\b\d{1,2}[\.:]\d{2}\s*WIB\b",
            RegexOptions.IgnoreCase);

        return timeMatch.Success
            ? Regex.Replace(timeMatch.Value.Trim(), @"\s+", " ").Replace(":", ".")
            : "";
    }

    private static string ExtractSpeed(string content)
    {
        var speedMatch = Regex.Match(
            content,
            @"\b\d+\s*km\s*/?\s*jam\b",
            RegexOptions.IgnoreCase);

        return speedMatch.Success
            ? Regex.Replace(speedMatch.Value.Trim(), @"\s+", " ")
            : "";
    }

    private static string ExtractCountBeforePhrase(string content, string phrase)
    {
        var match = Regex.Match(
            content,
            $@"\b(\d+)\s+{Regex.Escape(phrase)}\b",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value : "";
    }

    private static string FindFirstMatchingSentence(
        List<string> sentences,
        params string[] keywords)
    {
        return sentences.FirstOrDefault(sentence => ContainsAny(sentence, keywords)) ?? "";
    }

    private static string CapitalizeFirst(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string FindSopAccessSentence(List<RetrievedChunk> chunks)
    {
        var content = string.Join("\n", chunks.Select(x => x.Content));
        var sentences = ExtractSentences(content);

        return sentences.FirstOrDefault(sentence =>
            ContainsAny(
                sentence,
                "tangki penyimpanan",
                "area tangki",
                "hanya dapat diakses")) ?? "";
    }

    private static string? BuildProfileAnswer(List<RetrievedChunk> chunks, string question)
    {
        var fields = chunks
            .Select(x => (
                Field: ExtractContentLabel(x.Content, "Field"),
                Value: ExtractContentLabel(x.Content, "Value")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Field) &&
                        !string.IsNullOrWhiteSpace(x.Value))
            .ToList();

        if (!fields.Any())
            return null;

        if (ContainsAny(question, "nama unit", "unit perusahaan"))
            return FindProfileValue(fields, "Nama Unit");

        if (ContainsAny(question, "kapasitas", "kapasitas produksi"))
            return FindProfileValue(fields, "Kapasitas Produksi");

        if (ContainsAny(question, "lokasi", "alamat"))
            return FindProfileValue(fields, "Lokasi");

        if (ContainsAny(question, "jumlah karyawan"))
            return FindProfileValue(fields, "Jumlah Karyawan");

        if (ContainsAny(question, "jumlah shift", "shift operasional"))
            return FindProfileValue(fields, "Jumlah Shift Operasional");

        if (ContainsAny(question, "direktur operasi"))
            return FindProfileValue(fields, "Direktur Operasi");

        return string.Join(
            "\n",
            fields.Select(x => $"{x.Field}: {x.Value}"));
    }

    private static string? BuildMaintenanceFieldSpecificAnswer(
        RetrievedChunk chunk,
        string question)
    {
        var code = ValueOrFallback(
            chunk.MaintenanceCode,
            ChunkMetadataExtractor.ExtractMaintenanceCode(chunk.Content));

        var equipment = ValueOrFallback(
            chunk.Equipment,
            ChunkMetadataExtractor.ExtractEquipment(chunk.Content));

        var location = ValueOrFallback(
            chunk.Location,
            ChunkMetadataExtractor.ExtractLocation(chunk.Content));

        var status = ValueOrFallback(
            chunk.MaintenanceStatus,
            ChunkMetadataExtractor.ExtractMaintenanceStatus(chunk.Content));

        var technician = ValueOrFallback(
            chunk.Technician,
            ChunkMetadataExtractor.ExtractTechnician(chunk.Content));

        if (ContainsAny(question, "teknisi"))
            return $"Teknisi untuk {code} adalah {technician}.";

        if (ContainsAny(question, "status"))
            return $"Status peralatan {code} adalah {status}.";

        if (ContainsAny(question, "lokasi", "di mana", "dimana"))
            return $"Lokasi peralatan {code} adalah {location}.";

        if (ContainsAny(question, "peralatan", "alat"))
            return $"Peralatan dengan kode {code} adalah {equipment}.";

        return null;
    }

    private static string? FindProfileValue(
        IEnumerable<(string Field, string Value)> fields,
        string fieldName)
    {
        return fields
            .FirstOrDefault(x => x.Field.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    private static string ExtractContentLabel(string content, string label)
    {
        var match = Regex.Match(
            content,
            $@"(?im)^\s*{Regex.Escape(label)}:\s*(.+?)\s*$");

        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static bool IsStructuredRecord(RetrievedChunk chunk)
    {
        var recordType = ResolveRecordType(chunk);

        return recordType is "employee" or "overtime" or "maintenance";
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

    private static void AppendEmployeeAnswer(
    List<string> lines,
    List<RetrievedChunk> employeeChunks)
    {
        if (employeeChunks.Count == 1)
        {
            var chunk = employeeChunks.First();

            lines.Add("Data Karyawan:");
            lines.Add($@"- NIK: {ValueOrFallback(chunk.Nik, ChunkMetadataExtractor.ExtractNik(chunk.Content))}
  Nama: {ValueOrFallback(chunk.Name, ChunkMetadataExtractor.ExtractName(chunk.Content))}
  Divisi: {ValueOrFallback(chunk.Division, ChunkMetadataExtractor.ExtractDivision(chunk.Content))}
  Jabatan: {ValueOrFallback(chunk.Position, ChunkMetadataExtractor.ExtractPosition(chunk.Content))}
  Shift: {ValueOrFallback(chunk.Shift, ChunkMetadataExtractor.ExtractShift(chunk.Content))}
  Status: {ValueOrFallback(chunk.EmployeeStatus, ChunkMetadataExtractor.ExtractEmployeeStatus(chunk.Content))}");

            return;
        }

        lines.Add($"Ditemukan {employeeChunks.Count} data karyawan.");
        lines.Add("");
        lines.Add("| No | NIK | Nama | Divisi | Jabatan | Shift | Status |");
        lines.Add("|---|---|---|---|---|---|---|");

        for (int i = 0; i < employeeChunks.Count; i++)
        {
            var chunk = employeeChunks[i];

            var nik = ValueOrFallback(chunk.Nik, ChunkMetadataExtractor.ExtractNik(chunk.Content));
            var name = ValueOrFallback(chunk.Name, ChunkMetadataExtractor.ExtractName(chunk.Content));
            var division = ValueOrFallback(chunk.Division, ChunkMetadataExtractor.ExtractDivision(chunk.Content));
            var position = ValueOrFallback(chunk.Position, ChunkMetadataExtractor.ExtractPosition(chunk.Content));
            var shift = ValueOrFallback(chunk.Shift, ChunkMetadataExtractor.ExtractShift(chunk.Content));
            var status = ValueOrFallback(chunk.EmployeeStatus, ChunkMetadataExtractor.ExtractEmployeeStatus(chunk.Content));

            lines.Add($"| {i + 1} | {nik} | {name} | {division} | {position} | {shift} | {status} |");
        }
    }

    private static void AppendOvertimeAnswer(
    List<string> lines,
    List<RetrievedChunk> overtimeChunks)
    {
        if (overtimeChunks.Count == 1)
        {
            var chunk = overtimeChunks.First();

            lines.Add("Rekap Lembur:");
            lines.Add($@"- Tanggal: {ValueOrFallback(chunk.Date, ChunkMetadataExtractor.ExtractDate(chunk.Content))}
  Nama: {ValueOrFallback(chunk.Name, ChunkMetadataExtractor.ExtractName(chunk.Content))}
  Divisi: {ValueOrFallback(chunk.Division, ChunkMetadataExtractor.ExtractDivision(chunk.Content))}
  Durasi: {ValueOrFallback(chunk.Duration, ChunkMetadataExtractor.ExtractDuration(chunk.Content))}
  Approval: {ValueOrFallback(chunk.Approval, ChunkMetadataExtractor.ExtractApproval(chunk.Content))}");

            return;
        }

        lines.Add($"Ditemukan {overtimeChunks.Count} data rekap lembur.");
        lines.Add("");
        lines.Add("| No | Tanggal | Nama | Divisi | Durasi | Approval |");
        lines.Add("|---|---|---|---|---|---|");

        for (int i = 0; i < overtimeChunks.Count; i++)
        {
            var chunk = overtimeChunks[i];

            var date = ValueOrFallback(chunk.Date, ChunkMetadataExtractor.ExtractDate(chunk.Content));
            var name = ValueOrFallback(chunk.Name, ChunkMetadataExtractor.ExtractName(chunk.Content));
            var division = ValueOrFallback(chunk.Division, ChunkMetadataExtractor.ExtractDivision(chunk.Content));
            var duration = ValueOrFallback(chunk.Duration, ChunkMetadataExtractor.ExtractDuration(chunk.Content));
            var approval = ValueOrFallback(chunk.Approval, ChunkMetadataExtractor.ExtractApproval(chunk.Content));

            lines.Add($"| {i + 1} | {date} | {name} | {division} | {duration} | {approval} |");
        }
    }

    private static void AppendMaintenanceAnswer(
    List<string> lines,
    List<RetrievedChunk> maintenanceChunks)
    {
        if (maintenanceChunks.Count == 1)
        {
            var chunk = maintenanceChunks.First();

            lines.Add("Log Maintenance:");
            lines.Add($@"- Kode: {ValueOrFallback(chunk.MaintenanceCode, ChunkMetadataExtractor.ExtractMaintenanceCode(chunk.Content))}
  Peralatan: {ValueOrFallback(chunk.Equipment, ChunkMetadataExtractor.ExtractEquipment(chunk.Content))}
  Lokasi: {ValueOrFallback(chunk.Location, ChunkMetadataExtractor.ExtractLocation(chunk.Content))}
  Status: {ValueOrFallback(chunk.MaintenanceStatus, ChunkMetadataExtractor.ExtractMaintenanceStatus(chunk.Content))}
  Teknisi: {ValueOrFallback(chunk.Technician, ChunkMetadataExtractor.ExtractTechnician(chunk.Content))}");

            return;
        }

        lines.Add($"Ditemukan {maintenanceChunks.Count} data log maintenance.");
        lines.Add("");
        lines.Add("| No | Kode | Peralatan | Lokasi | Status | Teknisi |");
        lines.Add("|---|---|---|---|---|---|");

        for (int i = 0; i < maintenanceChunks.Count; i++)
        {
            var chunk = maintenanceChunks[i];

            var code = ValueOrFallback(chunk.MaintenanceCode, ChunkMetadataExtractor.ExtractMaintenanceCode(chunk.Content));
            var equipment = ValueOrFallback(chunk.Equipment, ChunkMetadataExtractor.ExtractEquipment(chunk.Content));
            var location = ValueOrFallback(chunk.Location, ChunkMetadataExtractor.ExtractLocation(chunk.Content));
            var status = ValueOrFallback(chunk.MaintenanceStatus, ChunkMetadataExtractor.ExtractMaintenanceStatus(chunk.Content));
            var technician = ValueOrFallback(chunk.Technician, ChunkMetadataExtractor.ExtractTechnician(chunk.Content));

            lines.Add($"| {i + 1} | {code} | {equipment} | {location} | {status} | {technician} |");
        }
    }
}
