using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Vev.Atlas.Domain;

namespace Vev.Atlas.Api;

/// <summary>
/// Maps domain errors to RFC 7807 problem responses. A denied access carries the machine-readable
/// reason code and source so a client (or the UI) can drive a reason-coded upgrade path (atlas#8),
/// never a bare status code.
/// </summary>
public sealed class AtlasExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var problem = exception switch
        {
            AccessDeniedException denied => new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Access denied",
                Detail = denied.Message,
                Extensions =
                {
                    ["reasonCode"] = denied.Decision.ReasonCode,
                    ["source"] = denied.Decision.Source
                }
            },
            CatalogueConflictException conflict => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = conflict.Message
            },
            CatalogueValidationException invalid => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request",
                Detail = invalid.Message
            },
            _ => null
        };

        if (problem is null)
        {
            return false; // Not ours — let the default 500 handler deal with it.
        }

        http.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails = problem
        });
    }
}
