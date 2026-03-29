namespace NotificationEngine.Application.Abstractions;

public interface ICommand<out TResponse> : MediatR.IRequest<TResponse>
{
}

public interface ICommandHandler<TCommand, TResponse> : MediatR.IRequestHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
}

public interface IQuery<out TResponse> : MediatR.IRequest<TResponse>
{
}

public interface IQueryHandler<TQuery, TResponse> : MediatR.IRequestHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
}
