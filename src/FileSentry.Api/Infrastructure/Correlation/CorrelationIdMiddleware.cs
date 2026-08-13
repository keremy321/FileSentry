using Microsoft.Extensions.Primitives;

namespace FileSentry.Api.Infrastructure.Correlation;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        StringValues suppliedValues = context.Request.Headers[CorrelationIdPolicy.HeaderName];
        string correlationId = suppliedValues.Count == 1
            && CorrelationIdPolicy.IsValid(suppliedValues[0])
                ? suppliedValues[0]!
                : CorrelationIdPolicy.Create();

        context.TraceIdentifier = correlationId;
        context.Response.Headers[CorrelationIdPolicy.HeaderName] = correlationId;
        await next(context);
    }
}
