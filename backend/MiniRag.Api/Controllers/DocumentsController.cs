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
        var path = indexing.FilePathFor(doc.Id, fileName);
        try
        {
            Directory.CreateDirectory(indexing.UploadDirectory);
            await using var target = System.IO.File.Create(path);
            await file.CopyToAsync(target, ct);
        }
        catch
        {
            // Client disconnected or disk error: don't leave a document that is Pending forever.
            registry.Remove(doc.Id);
            TryDeleteFile(path);
            throw;
        }

        indexing.QueueIndexing(doc.Id, fileName);
        return Accepted(doc);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var doc = registry.Get(id);
        if (doc is null)
            return NotFound();

        // Vectors first: if Qdrant is unavailable the document stays listed so the user can retry,
        // instead of vanishing from the list while its chunks keep being searched and cited.
        if (initializer.IsReady)
        {
            try
            {
                await store.DeleteDocumentAsync(id, ct);
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { error = $"Could not remove the document from the vector store: {ex.Message}" });
            }
        }

        registry.Remove(id);
        TryDeleteFile(indexing.FilePathFor(id, doc.FileName));
        return NoContent();
    }

    /// <summary>The indexer may still hold the file open (Windows); an orphaned upload is harmless.</summary>
    private static void TryDeleteFile(string path)
    {
        try { System.IO.File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
