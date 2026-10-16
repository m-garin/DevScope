using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DevScope.Api.Integrations.Mcp;

public sealed class McpExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<McpExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not McpIntegrationException)
        {
            return false;
        }

        logger.LogWarning("MCP request failed: {ErrorType}", exception.GetType().Name);
        httpContext.Response.StatusCode = StatusCodes.Status502BadGateway;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "Ошибка MCP",
                Detail = exception.Message
            },
            Exception = exception
        });
    }
}
