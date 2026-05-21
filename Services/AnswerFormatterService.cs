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
        if (retrievalMode == "profile")
        {
            return BuildProfileAnswer(chunks, question);
        }

        if (retrievalMode == "exact-maintenance-code")
        {
            var answer = BuildMaintenanceFieldSpecificAnswer(chunks.First(), question);

            if (!string.IsNullOrWhiteSpace(answer))
                return answer;
        }

        if (retrievalMode is "semantic" or "sop" or "audit")
        {
            return null;
        }

        return chunks.Any(IsStructuredRecord)
            ? BuildStructuredAnswer(chunks)
            : null;
    }

    private static string? BuildStructuredAnswer(List<RetrievedChunk> chunks)
    {
        var lines = new List<string>();

        var employeeChunks = chunks
            .Where(x => ResolveRecordType(x) == "employee")
            .ToList();

        if (employeeChunks.Any())
        {
            lines.Add("Data Karyawan:");

            foreach (var chunk in employeeChunks)
            {
                lines.Add($@"- NIK: {ValueOrFallback(chunk.Nik, ChunkMetadataExtractor.ExtractNik(chunk.Content))}
  Nama: {ValueOrFallback(chunk.Name, ChunkMetadataExtractor.ExtractName(chunk.Content))}
  Divisi: {ValueOrFallback(chunk.Division, ChunkMetadataExtractor.ExtractDivision(chunk.Content))}
  Jabatan: {ValueOrFallback(chunk.Position, ChunkMetadataExtractor.ExtractPosition(chunk.Content))}
  Shift: {ValueOrFallback(chunk.Shift, ChunkMetadataExtractor.ExtractShift(chunk.Content))}
  Status: {ValueOrFallback(chunk.EmployeeStatus, ChunkMetadataExtractor.ExtractEmployeeStatus(chunk.Content))}");
            }
        }

        var overtimeChunks = chunks
            .Where(x => ResolveRecordType(x) == "overtime")
            .ToList();

        if (overtimeChunks.Any())
        {
            if (lines.Any())
                lines.Add("");

            lines.Add("Rekap Lembur:");

            foreach (var chunk in overtimeChunks)
            {
                lines.Add($@"- Tanggal: {ValueOrFallback(chunk.Date, ChunkMetadataExtractor.ExtractDate(chunk.Content))}
  Nama: {ValueOrFallback(chunk.Name, ChunkMetadataExtractor.ExtractName(chunk.Content))}
  Divisi: {ValueOrFallback(chunk.Division, ChunkMetadataExtractor.ExtractDivision(chunk.Content))}
  Durasi: {ValueOrFallback(chunk.Duration, ChunkMetadataExtractor.ExtractDuration(chunk.Content))}
  Approval: {ValueOrFallback(chunk.Approval, ChunkMetadataExtractor.ExtractApproval(chunk.Content))}");
            }
        }

        var maintenanceChunks = chunks
            .Where(x => ResolveRecordType(x) == "maintenance")
            .ToList();

        if (maintenanceChunks.Any())
        {
            if (lines.Any())
                lines.Add("");

            lines.Add("Log Maintenance:");

            foreach (var chunk in maintenanceChunks)
            {
                lines.Add($@"- Kode: {ValueOrFallback(chunk.MaintenanceCode, ChunkMetadataExtractor.ExtractMaintenanceCode(chunk.Content))}
  Peralatan: {ValueOrFallback(chunk.Equipment, ChunkMetadataExtractor.ExtractEquipment(chunk.Content))}
  Lokasi: {ValueOrFallback(chunk.Location, ChunkMetadataExtractor.ExtractLocation(chunk.Content))}
  Status: {ValueOrFallback(chunk.MaintenanceStatus, ChunkMetadataExtractor.ExtractMaintenanceStatus(chunk.Content))}
  Teknisi: {ValueOrFallback(chunk.Technician, ChunkMetadataExtractor.ExtractTechnician(chunk.Content))}");
            }
        }

        return lines.Any() ? string.Join("\n", lines) : null;
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
}