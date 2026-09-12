namespace Library.Domain.Common;

/// <summary>
/// An <see cref="Entity"/> that records when it was created and last changed, and
/// that can raise domain events.
/// </summary>
/// <remarks>
/// The audit timestamps are written centrally by a <c>SaveChanges</c> interceptor
/// in the infrastructure layer rather than by each service. That guarantees they
/// cannot be forgotten, and keeps the "what time is it" dependency (<c>IClock</c>)
/// out of the domain.
/// </remarks>
public abstract class AuditableEntity : Entity, IHasDomainEvents
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>
    /// Events raised but not yet dispatched. Exposed read-only so callers cannot
    /// mutate the list behind the entity's back.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// <summary>Called by the dispatcher once the events have been handled.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>
/// Implemented by entities that collect domain events for post-commit dispatch.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
