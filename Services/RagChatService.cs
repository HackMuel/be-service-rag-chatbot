using be_service.Models;

namespace be_service.Services;

public class RagChatService
{
    private readonly OllamaService _ollamaService;
    private readonly QdrantService _qdrantService;

    public RagChatService(
        OllamaService ollamaService,
        QdrantService qdrantService)
    {
        _ollamaService = ollamaService;
        _qdrantService = qdrantService;
    }

    public async Task<ChatResponse> AskAsync(string question)
    {
        List<RetrievedChunk> chunks = new();

        var nikMatch = System.Text.RegularExpressions.Regex.Match(
            question,
            @"RU\s*6\s*-\s*\d{4}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );

        var isEmployeeQuery =
            question.Contains("karyawan", StringComparison.OrdinalIgnoreCase) ||
            question.Contains("nik", StringComparison.OrdinalIgnoreCase) ||
            question.Contains("nama", StringComparison.OrdinalIgnoreCase);

        if (nikMatch.Success)
        {
            var normalizedNik = System.Text.RegularExpressions.Regex.Replace(
                nikMatch.Value,
                @"\s+",
                ""
            ).ToUpper();

            chunks = await _qdrantService.SearchByKeywordAsync(normalizedNik);
        }
        else if (isEmployeeQuery)
        {
            var personKeyword = ExtractPersonKeyword(question);

            if (personKeyword.Split(" ", StringSplitOptions.RemoveEmptyEntries).Length >= 2)
            {
                chunks = await _qdrantService.SearchByKeywordAsync(personKeyword, 10);
            }
        }

        if (!chunks.Any())
        {
            var embedding =
                await _ollamaService.GenerateEmbeddingAsync(question);

            chunks =
                await _qdrantService.SearchAsync(embedding, 5);
        }

        var relevantChunks = chunks
            .OrderByDescending(x => x.Similarity)
            .Take(5)
            .ToList();

        Console.WriteLine("=== RETRIEVED CHUNKS ===");

        foreach (var chunk in relevantChunks)
        {
            Console.WriteLine(
                $"Source: {chunk.DocumentTitle} | Similarity: {chunk.Similarity:F2}");
        }

        if (!relevantChunks.Any())
        {
            return new ChatResponse
            {
                Answer = "Maaf, saya tidak menemukan informasi tersebut.",
                Sources = new List<string>(),
            };
        }

        var context = string.Join(
            "\n\n",
            relevantChunks.Select((x, index) =>
                $"[Sumber {index + 1}: {x.DocumentTitle}] | Similarity: {x.Similarity:F2}\n{x.Content}"
            ));

        var prompt = $@"
Kamu adalah AI assistant internal perusahaan.

ATURAN:
- Jawab HANYA berdasarkan context.
- Jangan membuat informasi sendiri.
- Jika informasi tidak ada di context, katakan:
  'Maaf, saya tidak menemukan informasi tersebut.'
- Jika ditemukan lebih dari satu orang dengan nama yang sama, tampilkan semuanya.
- Jangan menggabungkan Data Karyawan, Rekap Lembur, dan Log Maintenance menjadi satu record.
- Kelompokkan jawaban berdasarkan jenis data jika context berisi beberapa jenis data.
- Jika user menanyakan NIK seseorang, cari field NIK pada context.
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

        var answer =
            await _ollamaService.GenerateChatAsync(prompt);

        var sources = relevantChunks
            .Select(x => x.DocumentTitle)
            .Distinct()
            .ToList();

        return new ChatResponse
        {
            Answer = answer,
            Sources = sources,
        };
    }
    private static string ExtractPersonKeyword(string question)
    {
        return question
            .Replace("siapa", "", StringComparison.OrdinalIgnoreCase)
            .Replace("karyawan", "", StringComparison.OrdinalIgnoreCase)
            .Replace("dengan", "", StringComparison.OrdinalIgnoreCase)
            .Replace("nama", "", StringComparison.OrdinalIgnoreCase)
            .Replace("nik", "", StringComparison.OrdinalIgnoreCase)
            .Replace("berapa", "", StringComparison.OrdinalIgnoreCase)
            .Replace("nya", "", StringComparison.OrdinalIgnoreCase)
            .Replace("?", "")
            .Trim();
    }
}