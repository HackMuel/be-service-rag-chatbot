using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace be_service.Services;

public class PdfTextExtractor
{
    public Task<string> ExtractTextAsync(IFormFile file)
    {
        using var stream = file.OpenReadStream();
        using var pdf = PdfDocument.Open(stream);

        // Reconstruct visual lines per page so headings/subsections land on their
        // own line. The previous approach flattened each page into one long line,
        // which broke line-anchored heading detection (everything collapsed into a
        // single section chunk per page).
        var pageTexts = pdf.GetPages()
            .Select(ExtractPageText)
            .ToList();

        // Drop running header/footer lines (e.g. ".... Halaman 3") that repeat
        // across pages — they otherwise pollute every chunk.
        pageTexts = StripRepeatedLines(pageTexts);

        var text = string.Join(
            "\n\n",
            pageTexts.Where(p => !string.IsNullOrWhiteSpace(p)));

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new Exception("PDF tidak memiliki text yang bisa dibaca. Kemungkinan PDF berupa scan/gambar.");
        }

        return Task.FromResult(text);
    }

    // Groups a page's words into lines by their baseline (BoundingBox.Bottom),
    // ordered top-to-bottom then left-to-right.
    private static string ExtractPageText(Page page)
    {
        var words = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();

        if (words.Count == 0)
            return "";

        var heights = words
            .Select(w => w.BoundingBox.Height)
            .Where(h => h > 0)
            .OrderBy(h => h)
            .ToList();
        var medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 10.0;
        var yTolerance = medianHeight * 0.6; // words within this Y gap share a line

        var lines = new List<string>();
        var currentLine = new List<Word>();
        var currentY = double.NaN;

        foreach (var word in words
                     .OrderByDescending(w => w.BoundingBox.Bottom)
                     .ThenBy(w => w.BoundingBox.Left))
        {
            if (!double.IsNaN(currentY) &&
                Math.Abs(word.BoundingBox.Bottom - currentY) > yTolerance)
            {
                lines.Add(JoinLine(currentLine));
                currentLine.Clear();
            }

            currentLine.Add(word);
            currentY = word.BoundingBox.Bottom;
        }

        if (currentLine.Count > 0)
            lines.Add(JoinLine(currentLine));

        return string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    private static string JoinLine(List<Word> lineWords) =>
        string.Join(" ", lineWords
            .OrderBy(w => w.BoundingBox.Left)
            .Select(w => w.Text));

    // Removes lines that appear on many pages (running headers/footers). Page
    // numbers are masked first so "... Halaman 3" / "... Halaman 4" count as the
    // same recurring line.
    private static List<string> StripRepeatedLines(List<string> pages)
    {
        if (pages.Count < 3)
            return pages;

        static string Mask(string line) => Regex.Replace(line.Trim(), @"\d+", "#");

        var frequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        foreach (var line in page.Split('\n'))
        {
            var key = Mask(line);
            if (key.Length == 0)
                continue;
            frequency[key] = frequency.GetValueOrDefault(key) + 1;
        }

        var threshold = Math.Max(3, pages.Count / 2);
        var repeated = frequency
            .Where(kv => kv.Value >= threshold)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (repeated.Count == 0)
            return pages;

        return pages
            .Select(page => string.Join(
                "\n",
                page.Split('\n').Where(line => !repeated.Contains(Mask(line)))))
            .ToList();
    }
}
