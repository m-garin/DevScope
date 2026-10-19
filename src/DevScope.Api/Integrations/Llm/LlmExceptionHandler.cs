using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DevScope.Api.Integrations.Llm;

public sealed class LlmExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<LlmExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not LlmIntegrationException)
        {
            return false;
        }

        logger.LogWarning("LLM request failed: {ErrorType}", exception.GetType().Name);
        httpContext.Response.StatusCode = StatusCodes.Status502BadGateway;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "Ошибка LLM",
                Detail = exception.Message
            },
            Exception = exception
        });
    }
}
