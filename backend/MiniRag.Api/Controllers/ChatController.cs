using Microsoft.AspNetCore.Mvc;
using MiniRag.Api.Models;
using MiniRag.Api.Services;

namespace MiniRag.Api.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController(RagService rag) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ChatResponseDto>> Ask(ChatRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question must not be empty." });

        try
        {
            return await rag.AskAsync(request.Question.Trim(), ct);
        }
        catch (ServiceNotReadyException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not the client giving up: an HttpClient timeout talking to Ollama or Qdrant.
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                error = "Ollama or Qdrant did not respond in time. The model may still be loading; try again, " +
                        "or raise Ollama:TimeoutSeconds.",
            });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }
}
