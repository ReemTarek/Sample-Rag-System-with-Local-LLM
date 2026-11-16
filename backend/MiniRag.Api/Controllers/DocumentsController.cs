using Microsoft.AspNetCore.Mvc;
using MiniRag.Api.Infrastructure;
using MiniRag.Api.Models;
using MiniRag.Api.Services;

namespace MiniRag.Api.Controllers;

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(
    DocumentRegistry registry,
    DocumentIndexingService indexing,
    QdrantVectorStore store,
    VectorIndexInitializer initializer) : ControllerBase
{
    [HttpGet]
    public IReadOnlyList<DocumentDto> List() => registry.All();

    [HttpPost]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Choose a non-empty file to upload." });

        var fileName = Path.GetFileName(file.FileName);
        if (!DocumentTextExtractor.IsSupported(fileName))
            return BadRequest(new { error = "Unsupported file type. Upload a .pdf, .docx or .txt file." });

        var doc = registry.Add(fileName);
        Directory.CreateDirectory(indexing.UploadDirectory);
        await using (var target = System.IO.File.Create(indexing.FilePathFor(doc.Id, fileName)))
            await file.CopyToAsync(target, ct);

        indexing.QueueIndexing(doc.Id, fileName);
        return Accepted(doc);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var doc = registry.Get(id);
        if (doc is null)
            return NotFound();

        registry.Remove(id);
        if (initializer.IsReady)
            await store.DeleteDocumentAsync(id, ct);

        var path = indexing.FilePathFor(id, doc.FileName);
        if (System.IO.File.Exists(path))
            System.IO.File.Delete(path);
        return NoContent();
    }
}
