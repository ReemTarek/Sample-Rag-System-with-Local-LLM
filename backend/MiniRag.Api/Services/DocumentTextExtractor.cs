using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MiniRag.Api.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MiniRag.Api.Services;

/// <summary>Extracts plain text from PDF (per page), DOCX and TXT files.</summary>
public sealed class DocumentTextExtractor
{
    private static readonly string[] SupportedExtensions = [".pdf", ".docx", ".txt"];

    public static bool IsSupported(string fileName) =>
        SupportedExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <returns>Non-empty pages only. An empty list means the document has no extractable text.</returns>
    public IReadOnlyList<PageText> Extract(Stream content, string fileName)
    {
        var pages = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => ExtractPdf(content),
            ".docx" => [new PageText(null, ExtractDocx(content))],
            ".txt" => [new PageText(null, ExtractTxt(content))],
            var ext => throw new NotSupportedException(
                $"Unsupported file type '{ext}'. Supported: {string.Join(", ", SupportedExtensions)}."),
        };
        return pages.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList();
    }

    private static List<PageText> ExtractPdf(Stream content)
    {
        using var pdf = PdfDocument.Open(content);
        return pdf.GetPages()
            .Select(page => new PageText(page.Number, ContentOrderTextExtractor.GetText(page)))
            .ToList();
    }

    private static string ExtractDocx(Stream content)
    {
        using var doc = WordprocessingDocument.Open(content, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
            return string.Empty;

        // Paragraphs anywhere in the body (including inside tables), one per line.
        return string.Join('\n', body.Descendants<Paragraph>().Select(p => p.InnerText));
    }

    private static string ExtractTxt(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
