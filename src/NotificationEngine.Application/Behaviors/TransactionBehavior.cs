using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotificationEngine.Application.Abstractions;

namespace NotificationEngine.Application.Behaviors;

public class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<TransactionBehavior<TRequest, TResponse>> _logger;
    private readonly DbContext _dbContext;

    public TransactionBehavior(
        ILogger<TransactionBehavior<TRequest, TResponse>> logger,
        DbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Only apply transaction to commands (state-mutating operations)
        // Check if TRequest implements ICommand<TResponse>
        if (!typeof(ICommand<TResponse>).IsAssignableFrom(typeof(TRequest)))
        {
            return await next();
        }

        var requestName = typeof(TRequest).Name;

        if (_dbContext.Database.CurrentTransaction != null)
        {
            _logger.LogDebug(
                "Transaction already in progress for {RequestName}, skipping wrapper",
                requestName);
            return await next();
        }

        _logger.LogDebug(
            "Beginning transaction for {RequestName}",
            requestName);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            cancellationToken: cancellationToken);

        try
        {
            var response = await next();

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug(
                "Transaction committed for {RequestName}",
                requestName);

            return response;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);

            _logger.LogError(
                ex,
                "Transaction rolled back for {RequestName}",
                requestName);

            throw;
        }
    }
}
