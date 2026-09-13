using Library.Application.Common.Abstractions;
using Library.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Library.Infrastructure.Persistence;

/// <inheritdoc cref="IDomainEventDispatcher"/>
public sealed partial class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly LibraryDbContext _context;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DomainEventDispatcher> _logger;

    public DomainEventDispatcher(
        LibraryDbContext context,
        IServiceProvider serviceProvider,
        ILogger<DomainEventDispatcher> logger)
    {
        _context = context;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Dispatches every collected event, then clears them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Events are cleared before any handler runs.</b> A handler that writes
    /// through this same <c>DbContext</c> would otherwise have its own entities
    /// re-scanned by the next dispatch and its events replayed — and, worse, a
    /// handler that raises an event on an entity already in this batch would
    /// dispatch it twice. Snapshot, clear, then run.
    /// </para>
    /// <para>
    /// <b>One handler failing does not stop the others.</b> These are independent
    /// reactions to a fact that has already happened; a notification failing is no
    /// reason for a fine not to be assessed. Each failure is logged with the event
    /// type so it can be replayed, and none of them propagates — throwing here
    /// would surface a post-commit problem as a failure of the request that already
    /// succeeded, telling the caller their return did not happen when it did.
    /// </para>
    /// </remarks>
    public async Task DispatchAsync(CancellationToken cancellationToken = default)
    {
        List<IHasDomainEvents> entities = _context.ChangeTracker
            .Entries<IHasDomainEvents>()
            .Where(entry => entry.Entity.DomainEvents.Count > 0)
            .Select(entry => entry.Entity)
            .ToList();

        if (entities.Count == 0)
        {
            return;
        }

        List<IDomainEvent> events = entities
            .SelectMany(entity => entity.DomainEvents)
            .ToList();

        foreach (IHasDomainEvents entity in entities)
        {
            entity.ClearDomainEvents();
        }

        foreach (IDomainEvent domainEvent in events)
        {
            await DispatchOneAsync(domainEvent, cancellationToken);
        }
    }

    /// <summary>
    /// Resolves and invokes every handler registered for this event's runtime type.
    /// </summary>
    /// <remarks>
    /// The handler interface is closed over the concrete event type, so the
    /// service type has to be built at runtime from
    /// <c>domainEvent.GetType()</c> — the compiler only knows it as
    /// <see cref="IDomainEvent"/> here. <c>GetServices</c> rather than
    /// <c>GetService</c>: several things may legitimately react to one event, and
    /// the fine handler must not care whether a notification handler exists.
    /// </remarks>
    private async Task DispatchOneAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        Type handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());

        IEnumerable<object?> handlers = _serviceProvider.GetServices(handlerType);

        foreach (object? handler in handlers)
        {
            if (handler is null)
            {
                continue;
            }

            string eventName = domainEvent.GetType().Name;
            string handlerName = handler.GetType().Name;

            try
            {
                // HandleAsync is found by name because the handler is held as
                // object - the closed interface type is only known at runtime.
                var task = (Task?)handlerType
                    .GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!
                    .Invoke(handler, [domainEvent, cancellationToken]);

                if (task is not null)
                {
                    await task;
                }

                LogHandled(eventName, handlerName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Swallowed on purpose. The transaction has already committed; the
                // thing this event describes really happened. Failing the request
                // now would tell the caller otherwise.
                LogHandlerFailed(eventName, handlerName, ex);
            }
        }
    }

    [LoggerMessage(
        EventId = 6000,
        Level = LogLevel.Debug,
        Message = "Dispatched {EventName} to {HandlerName}")]
    private partial void LogHandled(string eventName, string handlerName);

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Error,
        Message = "Handler {HandlerName} failed for {EventName}. "
                  + "The originating transaction has already committed and is not rolled back.")]
    private partial void LogHandlerFailed(string eventName, string handlerName, Exception exception);
}
