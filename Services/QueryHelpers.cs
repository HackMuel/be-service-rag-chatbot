using System.Text.RegularExpressions;
using be_service.Models;

namespace be_service.Services;

public static class QueryHelpers
{
    public static bool ContainsAny(string value, params string[] keywords) =>
        keywords.Any(k => value.Contains(k, StringComparison.OrdinalIgnoreCase));

    public static string ResolveRecordType(RetrievedChunk chunk) =>
        string.IsNullOrWhiteSpace(chunk.RecordType)
            ? ChunkMetadataExtractor.DetectRecordType(chunk.Content)
            : chunk.RecordType;

    // Splits content into sentences, stripping leading bullet/number prefixes.
    // Use for building SOP lists, narrative answers, structured extraction.
    public static List<string> ExtractSentences(string content) =>
        Regex.Split(content, @"(?<=[.!?])\s+|\r?\n+")
            .Select(x => Regex.Replace(x, @"^\s*[-\d.)]+\s*", "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

    // Splits content into raw segments without stripping prefixes.
    // Use for LLM context cleaning where preserving segment structure matters.
    public static IEnumerable<string> SplitContentSegments(string content) =>
        Regex.Split(content, @"(?<=[.!?])\s+|\r?\n+");
}
