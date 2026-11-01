using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EfAuditTrail.Api.Infrastructure;

/// <summary>Thrown by handlers for a request that cannot be served as asked.</summary>
public sealed class ValidationException(string message) : Exception(message);

/// <summary>Maps <see cref="ValidationException"/> to 400 and a missing row to 404, as problem details.</summary>
public sealed class ValidationExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "The request is not valid."),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found."),
            _ => (0, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = exception.Message },
        });
    }
}
