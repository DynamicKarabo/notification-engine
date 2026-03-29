using MediatR;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace NotificationEngine.Application.Behaviors;

public class PerformanceBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<PerformanceBehavior<TRequest, TResponse>> _logger;
    private const long ThresholdMs = 500;

    public PerformanceBehavior(ILogger<PerformanceBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var response = await next();

        stopwatch.Stop();

        var elapsedMs = stopwatch.ElapsedMilliseconds;

        if (elapsedMs > ThresholdMs)
        {
            var requestName = typeof(TRequest).Name;

            _logger.LogWarning(
                "[PERFORMANCE] {RequestName} took {ElapsedMs}ms (threshold: {ThresholdMs}ms)",
                requestName,
                elapsedMs,
                ThresholdMs);
        }

        return response;
    }
}
