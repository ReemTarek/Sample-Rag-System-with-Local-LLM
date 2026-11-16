using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MiniRag.Api.Services;
using PdfPageSize = UglyToad.PdfPig.Content.PageSize;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MiniRag.Tests;

public class DocumentTextExtractorTests
{
    private readonly DocumentTextExtractor _extractor = new();

    [Fact]
    public void Extract_Txt_ReturnsSinglePageWithNullPageNumber()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Line one.\nLine two."));

        var pages = _extractor.Extract(stream, "notes.TXT");

        var page = Assert.Single(pages);
        Assert.Null(page.PageNumber);
        Assert.Equal("Line one.\nLine two.", page.Text);
    }

    [Fact]
    public void Extract_EmptyTxt_ReturnsNoPages()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("  \n "));

        Assert.Empty(_extractor.Extract(stream, "empty.txt"));
    }

    [Fact]
    public void Extract_Docx_ReturnsParagraphText()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("First paragraph."))),
                new Paragraph(new Run(new Text("Second "), new Text("paragraph.") { Space = SpaceProcessingModeValues.Preserve }))));
        }
        stream.Position = 0;

        var pages = _extractor.Extract(stream, "report.docx");

        var page = Assert.Single(pages);
        Assert.Null(page.PageNumber);
        Assert.Equal("First paragraph.\nSecond paragraph.", page.Text);
    }

    [Fact]
    public void Extract_Pdf_ReturnsTextPerPage()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PdfPageSize.A4).AddText("Apples are red", 12, new PdfPoint(25, 700), font);
        builder.AddPage(PdfPageSize.A4).AddText("Bananas are yellow", 12, new PdfPoint(25, 700), font);
        using var stream = new MemoryStream(builder.Build());

        var pages = _extractor.Extract(stream, "fruit.pdf");

        Assert.Equal(2, pages.Count);
        Assert.Equal(1, pages[0].PageNumber);
        Assert.Contains("Apples are red", pages[0].Text);
        Assert.Equal(2, pages[1].PageNumber);
        Assert.Contains("Bananas are yellow", pages[1].Text);
    }

    [Fact]
    public void Extract_UnsupportedExtension_Throws()
    {
        using var stream = new MemoryStream([1, 2, 3]);

        Assert.Throws<NotSupportedException>(() => _extractor.Extract(stream, "sheet.xlsx"));
    }

    [Theory]
    [InlineData("a.pdf", true)]
    [InlineData("a.DOCX", true)]
    [InlineData("a.txt", true)]
    [InlineData("a.doc", false)]
    [InlineData("noextension", false)]
    public void IsSupported_ChecksExtension(string fileName, bool expected)
    {
        Assert.Equal(expected, DocumentTextExtractor.IsSupported(fileName));
    }
}
