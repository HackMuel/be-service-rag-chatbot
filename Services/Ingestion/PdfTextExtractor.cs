using UglyToad.PdfPig;

namespace be_service.Services;

public class PdfTextExtractor
{
    public Task<string> ExtractTextAsync(IFormFile file)
    {
        using var stream = file.OpenReadStream();
        using var pdf = PdfDocument.Open(stream);

        var text = string.Join("\n\n",
            pdf.GetPages().Select(page =>
                string.Join(" ", page.GetWords().Select(w => w.Text))
            ));

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new Exception("PDF tidak memiliki text yang bisa dibaca. Kemungkinan PDF berupa scan/gambar.");
        }

        return Task.FromResult(text);
    }
}
