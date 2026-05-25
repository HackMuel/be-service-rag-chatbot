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
        "informasi",
        "lembur",
        "maintenance",
        "log",
        "sop",
        "audit",
        "dokumen",
        "perusahaan",
        "apa",
        "siapa",
        "berikan",
        "tampilkan",
        "divisi",
        "lokasi",
        "status",
        "shift",
        "jabatan",
        "di",
        "gak",
        "nggak"
    };

    private readonly ChunkRepository _chunkRepository;
    private readonly ILogger<StructuredEntityResolver> _logger;

    public StructuredEntityResolver(
        ChunkRepository chunkRepository,
        ILogger<StructuredEntityResolver> logger)
    {
        _chunkRepository = chunkRepository;
        _logger = logger;
    }

    public async Task<StructuredEntityMatch?> ResolveAsync(
        string question,
        RagQueryAnalysis analysis)
    {
        var normalizedQuestion = NormalizeForFuzzy(question);

        if (string.IsNullOrWhiteSpace(normalizedQuestion))
        {
            return null;
        }

        var knownEntities = await _chunkRepository.GetKnownStructuredEntitiesAsync();

        var exactMatch = knownEntities
            .Select(entity => BuildMatch(entity, normalizedQuestion, analysis))
            .Where(match => match is not null)
            .OrderByDescending(match => match!.Priority)
            .FirstOrDefault();

        if (exactMatch is not null)
        {
            LogMatch(exactMatch);
            return exactMatch;
        }

        var fuzzyMatches = knownEntities
            .Select(entity => BuildFuzzyMatch(entity, normalizedQuestion, analysis))
            .Where(match => match is not null)
            .OrderByDescending(match => match!.Score)
            .ThenByDescending(match => match!.Priority)
            .ToList();

        var bestMatch = ResolveBestFuzzyMatch(fuzzyMatches);

        if (bestMatch is not null)
        {
            LogMatch(bestMatch);
        }

        return bestMatch;
    }

    private static StructuredEntityMatch? BuildMatch(
        StructuredEntityMatch entity,
        string normalizedQuestion,
        RagQueryAnalysis analysis)
    {
        var normalizedValue = NormalizeForFuzzy(entity.Value);

        if (string.IsNullOrWhiteSpace(normalizedValue) ||
            IsGenericValue(normalizedValue))
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
            Priority = CalculatePriority(entity, normalizedValue, analysis),
            Score = 1,
            MatchType = "exact"
        };
    }

    private static StructuredEntityMatch? BuildFuzzyMatch(
        StructuredEntityMatch entity,
        string normalizedQuestion,
        RagQueryAnalysis analysis)
    {
        var normalizedValue = NormalizeForFuzzy(entity.Value);

        if (string.IsNullOrWhiteSpace(normalizedValue) ||
            IsGenericValue(normalizedValue) ||
            !IsFieldRelevant(entity.FieldName, analysis) ||
            !CanFuzzyMatchField(entity.FieldName))
        {
            return null;
        }

        if (normalizedValue.Length < 4)
        {
            return null;
        }

        var entityTokens = SplitTokens(normalizedValue);

        if (!entityTokens.Any())
        {
            return null;
        }

        if (entityTokens.Count == 1 && entityTokens[0].Length < 4)
        {
            return null;
        }

        var queryTokens = SplitTokens(normalizedQuestion)
            .Where(token => !IsGenericValue(token))
            .ToList();

        if (queryTokens.Count < entityTokens.Count)
        {
            return null;
        }

        var bestScore = 0d;
        var bestDistance = int.MaxValue;

        for (var i = 0; i <= queryTokens.Count - entityTokens.Count; i++)
        {
            var window = string.Join(" ", queryTokens.Skip(i).Take(entityTokens.Count));
            var distance = CalculateLevenshteinDistance(window, normalizedValue);
            var score = CalculateSimilarity(window, normalizedValue, distance);

            if (score > bestScore)
            {
                bestScore = score;
                bestDistance = distance;
            }
        }

        if (!PassesFuzzyThreshold(bestScore, bestDistance, entityTokens))
        {
            return null;
        }

        return new StructuredEntityMatch
        {
            FieldName = entity.FieldName,
            Value = entity.Value,
            RecordType = entity.RecordType,
            Priority = CalculatePriority(entity, normalizedValue, analysis),
            Score = bestScore,
            MatchType = "fuzzy"
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

    private static StructuredEntityMatch? ResolveBestFuzzyMatch(
        List<StructuredEntityMatch?> fuzzyMatches)
    {
        var matches = fuzzyMatches
            .Where(match => match is not null)
            .Cast<StructuredEntityMatch>()
            .ToList();

        if (!matches.Any())
        {
            return null;
        }

        var best = matches.First();
        var second = matches.Skip(1).FirstOrDefault();

        if (second is not null &&
            Math.Abs(best.Score - second.Score) < 0.03 &&
            (!best.FieldName.Equals(second.FieldName, StringComparison.OrdinalIgnoreCase) ||
             !best.Value.Equals(second.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return best;
    }

    private static bool CanFuzzyMatchField(string fieldName)
    {
        return fieldName is
            "name" or
            "division" or
            "position" or
            "employeeStatus" or
            "approval" or
            "maintenanceStatus" or
            "location" or
            "equipment" or
            "technician";
    }

    private static bool PassesFuzzyThreshold(
        double score,
        int distance,
        List<string> entityTokens)
    {
        if (entityTokens.Count > 1)
        {
            return score >= 0.85;
        }

        var tokenLength = entityTokens[0].Length;

        if (tokenLength < 4)
        {
            return false;
        }

        return score >= 0.90 ||
               (tokenLength >= 7 && distance == 1);
    }

    private static List<string> SplitTokens(string value)
    {
        return value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static bool IsGenericValue(string value)
    {
        return GenericValues.Contains(value);
    }

    private static int CalculateLevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a))
            return string.IsNullOrEmpty(b) ? 0 : b.Length;

        if (string.IsNullOrEmpty(b))
            return a.Length;

        var costs = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            costs[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            var previousDiagonal = costs[0];
            costs[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var temp = costs[j];
                var substitutionCost = a[i - 1] == b[j - 1] ? 0 : 1;

                costs[j] = Math.Min(
                    Math.Min(costs[j] + 1, costs[j - 1] + 1),
                    previousDiagonal + substitutionCost);

                previousDiagonal = temp;
            }
        }

        return costs[b.Length];
    }

    private static double CalculateSimilarity(
        string a,
        string b,
        int distance)
    {
        var maxLength = Math.Max(a.Length, b.Length);

        if (maxLength == 0)
        {
            return 1;
        }

        return 1 - (double)distance / maxLength;
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

    private static string NormalizeForFuzzy(string value)
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

    private void LogMatch(StructuredEntityMatch match)
    {
        _logger.LogInformation(
            "STRUCTURED_ENTITY_MATCH field={FieldName}, matchType={MatchType}, score={Score:F2}",
            match.FieldName,
            match.MatchType,
            match.Score);
    }
}
