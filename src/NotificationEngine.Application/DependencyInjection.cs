using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NotificationEngine.Application.Abstractions;
using NotificationEngine.Application.Behaviors;

namespace NotificationEngine.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(ICommand<>).Assembly);
            
            // Register pipeline behaviors in ORDER (first to last = outer to inner execution)
            // 1. LoggingBehaviour - Entry/exit logging with timing
            // 2. ValidationBehaviour - FluentValidation
            // 3. PerformanceBehaviour - Warn if handler > 500ms
            // 4. TransactionBehaviour - Wrap commands in DB transaction (queries bypassed via ICommand<T>)
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(PerformanceBehavior<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
        });

        services.AddValidatorsFromAssembly(typeof(ICommand<>).Assembly, includeInternalTypes: true);

        return services;
    }
}
