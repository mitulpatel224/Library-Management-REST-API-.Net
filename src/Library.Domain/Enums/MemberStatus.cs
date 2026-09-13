namespace Library.Domain.Enums;

/// <summary>
/// Borrowing eligibility of a <see cref="Entities.Member"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="Active"/> may borrow. The other three are distinct on purpose
/// — collapsing them into a single <c>IsActive</c> boolean would lose the reason,
/// and the reason is what decides whether the member can be reinstated, and by
/// whom.
/// </para>
/// <para>
/// Values are explicitly numbered because they are persisted. Letting the
/// compiler assign them means inserting a member in the middle silently
/// renumbers every value after it — and reinterprets existing rows.
/// </para>
/// </remarks>
public enum MemberStatus
{
    /// <summary>In good standing. The only status that may borrow.</summary>
    Active = 0,

    /// <summary>
    /// Borrowing privileges withdrawn — unpaid fines, repeated damage.
    /// A librarian decision, and a librarian can reverse it.
    /// </summary>
    Suspended = 1,

    /// <summary>
    /// Membership lapsed through time rather than conduct. Reinstated by
    /// renewing, not by appeal.
    /// </summary>
    Expired = 2,

    /// <summary>
    /// Closed at the member's own request. Retained rather than deleted so loan
    /// history survives — and because a foreign key from a historical loan would
    /// block the delete anyway.
    /// </summary>
    Cancelled = 3,
}
