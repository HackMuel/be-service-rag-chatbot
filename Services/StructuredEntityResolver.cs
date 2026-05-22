using System.Text.RegularExpressions;
using be_service.Models;
using be_service.Repositories;

namespace be_service.Services;

public class StructuredEntityResolver
{
    private static readonly HashSet<string> GenericValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "data",
        "karyawan",
        "pegawai",
        "lembur",
        "maintenance",
        "log",
        "sop",
        "audit"
    };

    private readonly ChunkRepository _chunkRepository;

    public StructuredEntityResolver(ChunkRepository chunkRepository)
    {
        _chunkRepository = chunkRepository;
    }

    public async Task<StructuredEntityMatch?> ResolveAsync(
        string question,
        RagQueryAnalysis analysis)
    {
        var normalizedQuestion = Normalize(question);

        if (string.IsNullOrWhiteSpace(normalizedQuestion))
        {
            return null;
        }

        var knownEntities = await _chunkRepository.GetKnownStructuredEntitiesAsync();

        return knownEntities
            .Select(entity => BuildMatch(entity, normalizedQuestion, analysis))
            .Where(match => match is not null)
            .OrderByDescending(match => match!.Priority)
            .FirstOrDefault();
    }

    private static StructuredEntityMatch? BuildMatch(
        StructuredEntityMatch entity,
        string normalizedQuestion,
        RagQueryAnalysis analysis)
    {
        var normalizedValue = Normalize(entity.Value);

        if (string.IsNullOrWhiteSpace(normalizedValue) ||
            GenericValues.Contains(normalizedValue))
        {
            return null;
        }

        if (!IsFieldRelevant(entity.FieldName, analysis))
        {
            return null;
        }

        if (!ContainsEntity(normalizedQuestion, normalizedValue, entity.FieldName))
        {
            return null;
        }

        return new StructuredEntityMatch
        {
            FieldName = entity.FieldName,
            Value = entity.Value,
            RecordType = entity.RecordType,
            Priority = CalculatePriority(entity, normalizedValue, analysis)
        };
    }

    private static bool IsFieldRelevant(string fieldName, RagQueryAnalysis analysis)
    {
        if (analysis.IsEmployeeQuery)
        {
            return fieldName is "name" or "division" or "position" or "shift" or "employeeStatus";
        }

        if (analysis.IsOvertimeQuery)
        {
            return fieldName is "name" or "division" or "approval";
        }

        if (analysis.IsMaintenanceQuery)
        {
            return fieldName is "maintenanceStatus" or "location" or "equipment" or "technician";
        }

        return fieldName is
            "name" or
            "division" or
            "position" or
            "shift" or
            "employeeStatus" or
            "approval" or
            "maintenanceStatus" or
            "location" or
            "equipment" or
            "technician";
    }

    private static bool ContainsEntity(
        string normalizedQuestion,
        string normalizedValue,
        string fieldName)
    {
        if (fieldName == "shift")
        {
            return Regex.IsMatch(
                normalizedQuestion,
                $@"\bshift\s+{Regex.Escape(normalizedValue)}\b",
                RegexOptions.IgnoreCase);
        }

        return Regex.IsMatch(
            normalizedQuestion,
            $@"(^|\s){Regex.Escape(normalizedValue)}($|\s)",
            RegexOptions.IgnoreCase);
    }

    private static int CalculatePriority(
        StructuredEntityMatch entity,
        string normalizedValue,
        RagQueryAnalysis analysis)
    {
        var priority = normalizedValue.Length;

        priority += entity.FieldName switch
        {
            "name" => 1000,
            "equipment" => 900,
            "location" => 850,
            "technician" => 800,
            "maintenanceStatus" => 750,
            "division" => 700,
            "approval" => 650,
            "employeeStatus" => 600,
            "position" => 550,
            "shift" => 500,
            _ => 0
        };

        if (analysis.IsMaintenanceQuery &&
            entity.FieldName is "equipment" or "location" or "technician" or "maintenanceStatus")
        {
            priority += 1000;
        }

        if (analysis.IsOvertimeQuery &&
            entity.FieldName is "name" or "division" or "approval")
        {
            priority += 1000;
        }

        if (analysis.IsEmployeeQuery &&
            entity.FieldName is "name" or "division" or "position" or "shift" or "employeeStatus")
        {
            priority += 1000;
        }

        return priority;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = value.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}\s&-]", " ");
        normalized = normalized.Replace("&", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        return normalized;
    }
}
