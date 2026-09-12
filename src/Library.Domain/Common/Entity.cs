namespace Library.Domain.Common;

/// <summary>
/// Base class for every persistent domain object.
/// </summary>
/// <remarks>
/// <para>
/// Identity is modelled as an <see cref="int"/> surrogate key. Two consequences
/// worth being explicit about:
/// </para>
/// <list type="bullet">
///   <item>
///     Equality is by identity, not by value. Two <c>Book</c> instances with the
///     same <see cref="Id"/> are the same book even if a field differs, because
///     they refer to the same row. Value objects (Email, Isbn) get the opposite
///     treatment and are modelled as records.
///   </item>
///   <item>
///     Sequential integer keys are enumerable. <c>/api/members/1</c>,
///     <c>/api/members/2</c>, ... invites an object-level authorization attack
///     (OWASP API1: Broken Object Level Authorization). The mitigation is
///     authorization on every resource access, not key obfuscation - that is
///     addressed in Phase 5 and audited in Phase 8.
///   </item>
/// </list>
/// <para>
/// An entity that has never been saved has <see cref="Id"/> == 0 and is
/// <see cref="IsTransient"/>. Two transient entities are only equal by reference.
/// </para>
/// </remarks>
public abstract class Entity : IEquatable<Entity>
{
    /// <summary>Database-generated surrogate key. Zero until first persisted.</summary>
    public int Id { get; protected set; }

    /// <summary>True when this instance has not yet been assigned a key by the database.</summary>
    public bool IsTransient => Id == 0;

    public bool Equals(Entity? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        // Different concrete types are never equal, even with matching ids -
        // Book 5 is not Member 5.
        if (GetType() != other.GetType()) return false;

        // Unsaved entities have no identity to compare, so fall back to reference
        // equality (already checked above, hence false here).
        if (IsTransient || other.IsTransient) return false;

        return Id == other.Id;
    }

    public override bool Equals(object? obj) => Equals(obj as Entity);

    public override int GetHashCode() =>
        IsTransient ? base.GetHashCode() : HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity? left, Entity? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(Entity? left, Entity? right) => !(left == right);
}
