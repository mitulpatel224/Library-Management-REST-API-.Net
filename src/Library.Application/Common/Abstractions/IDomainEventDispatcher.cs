using Library.Domain.Common;

namespace Library.Application.Common.Abstractions;

/// <summary>
/// Handles one kind of domain event.
/// </summary>
/// <remarks>
/// <para>
/// Handlers run <b>after</b> the transaction that raised the event has committed.
/// That has a consequence worth stating plainly: a handler cannot undo the thing
/// that happened. If assessing a fine fails, the return has already been recorded
/// and the copy is already back on the shelf — the correct response is to log and
/// retry, never to try to roll the return back.
/// </para>
/// <para>
/// This is a deliberate trade. Dispatching inside the transaction would make the
/// two atomic, but it would also mean a slow or failing handler could roll back a
/// return that genuinely happened at the desk. Between "a fine may be late" and
/// "a return may be lost", the first is the better failure.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">The event this handler responds to.</typeparam>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Collects domain events raised during a unit of work and dispatches them once
/// it has committed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than a C# <c>event</c>.</b> A C# event invokes its
/// subscribers synchronously and inline, at the instant it is raised — which here
/// would be <i>before</i> <c>SaveChangesAsync</c>. If the transaction then rolled
/// back, a fine would already have been assessed for a return that never happened,
/// and there would be no record that it was wrong.
/// </para>
/// <para>
/// So entities only <i>collect</i>. Nothing is invoked until the data is durable.
/// The project still uses the C# <c>event</c> keyword deliberately — in
/// <c>INotificationService</c>, where fire-and-forget in-process notification is
/// the correct semantic and nobody is harmed by a missed subscriber. The contrast
/// between the two mechanisms is the point.
/// </para>
/// </remarks>
public interface IDomainEventDispatcher
{
    /// <summary>
    /// Dispatches every event collected by the tracked entities, then clears them.
    /// </summary>
    Task DispatchAsync(CancellationToken cancellationToken = default);
}
