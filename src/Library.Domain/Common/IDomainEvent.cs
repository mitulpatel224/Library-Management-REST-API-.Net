namespace Library.Domain.Common;

/// <summary>
/// A record of something that has already happened in the domain.
/// </summary>
/// <remarks>
/// <para>
/// Domain events are named in the <b>past tense</b> (<c>LoanReturnedEvent</c>,
/// not <c>ReturnLoanCommand</c>) because they describe a fact, not a request.
/// A handler may not veto them.
/// </para>
/// <para>
/// <b>Why not a plain C# <c>event</c>?</b> A C# <c>event</c> invokes its
/// subscribers synchronously, in-line, at the moment it is raised - which here
/// would be <i>before</i> the database transaction commits. If the transaction
/// then rolled back, a fine would have been assessed for a return that never
/// happened. So entities only <i>collect</i> events (see
/// <see cref="IHasDomainEvents"/>), and the infrastructure dispatches them after
/// <c>SaveChangesAsync</c> succeeds.
/// </para>
/// <para>
/// The project also demonstrates the C# <c>event</c> keyword deliberately, in
/// the notification service, where fire-and-forget in-process notification is
/// the correct semantic. The two mechanisms solve different problems and the
/// contrast is the point.
/// </para>
/// </remarks>
public interface IDomainEvent
{
    /// <summary>When the event occurred. Set at construction, never mutated.</summary>
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Convenience base for domain events; stamps <see cref="OccurredAt"/> on creation.
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
