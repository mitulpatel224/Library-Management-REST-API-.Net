using Library.Domain.Common;
using Library.Domain.Enums;
using Library.Domain.Exceptions;
using Library.Domain.ValueObjects;

namespace Library.Domain.Entities;

/// <summary>
/// A library member — the person who borrows copies.
/// </summary>
/// <remarks>
/// Phase 5 adds a <c>UserId</c> foreign key linking a member to their login,
/// which is what lets <c>GET /api/loans/me</c> return only the caller's own
/// records. It is deliberately absent until then: a nullable column with no
/// reader is a column that drifts out of sync before anything depends on it.
/// </remarks>
public sealed class Member : AuditableEntity
{
    /// <summary>Matches the column width in <c>MemberConfiguration</c>.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The earliest join date this system will issue a membership for.</summary>
    /// <remarks>
    /// The join year is baked into the membership number, and that number is
    /// immutable once issued - so a typo of 1825 for 2025 prints a card that is
    /// wrong for the life of the membership. A floor of 1900 rejects the
    /// transposition and mis-keyed century without a guess at when this
    /// particular library opened; a real deployment should narrow it to its own
    /// founding date.
    /// </remarks>
    public static readonly DateOnly EarliestJoinDate = new(1900, 1, 1);

    private Member() { }

    private Member(
        string fullName,
        Email email,
        PhoneNumber? phone,
        int membershipTypeId,
        DateOnly joinedOn,
        string? address)
    {
        FullName = fullName;
        Email = email.Value;
        Phone = phone?.Value;
        MembershipTypeId = membershipTypeId;
        JoinedOn = joinedOn;
        Address = address;
        Status = MemberStatus.Active;

        // A UNIQUE placeholder, not a blank. See MembershipNumber for why the
        // obvious empty-string default is a bug.
        MembershipNumber = NewPendingNumber();
    }

    // -----------------------------------------------------------------------
    // Membership number
    // -----------------------------------------------------------------------

    /// <summary>
    /// Human-facing identifier, e.g. <c>MEM-2026-00042</c>. Unique library-wide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Derived from the surrogate key, not generated independently.</b> The
    /// obvious alternatives are both worse:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Count existing members and add one</b> — a check-then-act race.
    ///     Two concurrent registrations read the same count and claim the same
    ///     number; the unique index rejects one and a legitimate registration
    ///     fails.</item>
    ///   <item><b>A random or GUID-based number</b> — collision-free, but a
    ///     librarian has to read it aloud over a desk. <c>MEM-2026-00042</c> is
    ///     something a person can say.</item>
    /// </list>
    /// <para>
    /// Deriving it from <c>Id</c> inherits the database's own uniqueness
    /// guarantee, needs no coordination, and stays readable. The cost is that it
    /// cannot be known until after the insert, which is why
    /// <see cref="AssignMembershipNumber"/> exists and why creating a member is
    /// two saves inside one transaction.
    /// </para>
    /// <para>
    /// It also leaks the member count and registration order, which is
    /// acceptable here: it is an internal library identifier, not a secret, and
    /// authorization never depends on it.
    /// </para>
    /// <para>
    /// <b>Why the pre-save value is a unique placeholder rather than blank.</b>
    /// The column carries a UNIQUE index, and the number cannot be computed
    /// until the insert has produced an id — so something has to occupy the
    /// column in between. An empty string cannot: insert two members and the
    /// index rejects the second, because <c>""</c> equals <c>""</c>.
    /// </para>
    /// <para>
    /// That failure is not limited to batch inserts. Two concurrent
    /// registrations would each insert a blank and the second would be rejected,
    /// turning an ordinary sign-up into a spurious conflict. A per-instance
    /// placeholder makes every row distinct from creation onward, so the index
    /// is satisfied at every moment.
    /// </para>
    /// </remarks>
    public string MembershipNumber { get; private set; } = string.Empty;

    /// <summary>Prefix marking a number that has not yet been issued.</summary>
    public const string PendingPrefix = "PENDING-";

    /// <summary>True until the real membership number has been assigned.</summary>
    public bool HasPendingMembershipNumber =>
        MembershipNumber.StartsWith(PendingPrefix, StringComparison.Ordinal);

    /// <summary>Builds the canonical membership number for a year and id.</summary>
    public static string FormatMembershipNumber(int year, int id) => $"MEM-{year}-{id:D5}";

    /// <summary>
    /// A collision-resistant temporary value, exactly 20 characters to fit the
    /// column. Never visible outside the transaction that creates the member.
    /// </summary>
    private static string NewPendingNumber() =>
        PendingPrefix + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    /// <summary>
    /// Replaces the placeholder with the real number, once the database has
    /// issued an id.
    /// </summary>
    /// <remarks>
    /// Idempotent, and refuses to renumber a member who already has a real
    /// number — a membership number is printed on a physical card, so changing
    /// it silently would invalidate something the member is holding.
    /// </remarks>
    public void AssignMembershipNumber()
    {
        if (!HasPendingMembershipNumber)
        {
            return;
        }

        if (IsTransient)
        {
            throw new BusinessRuleViolationException(
                "member.number_before_save",
                "A membership number cannot be assigned before the member is saved.");
        }

        MembershipNumber = FormatMembershipNumber(JoinedOn.Year, Id);
    }

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    public string FullName { get; private set; } = null!;

    /// <summary>
    /// The member's email, normalised to lower case. Unique library-wide.
    /// </summary>
    /// <remarks>
    /// A plain string for the same reason as <see cref="Book.Isbn"/>: a value
    /// object mapped through an EF converter cannot be used in <c>LIKE</c> or
    /// <c>ORDER BY</c>, and members are searched by email. The
    /// <see cref="ValueObjects.Email"/> type still guards construction — see
    /// <see cref="Create"/> — so an invalid address cannot reach this property.
    /// </remarks>
    public string Email { get; private set; } = null!;

    /// <summary>
    /// Optional phone number, normalised to digits with an optional leading
    /// <c>+</c>. Null when the member registered without one.
    /// </summary>
    public string? Phone { get; private set; }

    public string? Address { get; private set; }

    public int MembershipTypeId { get; private set; }

    public MembershipType MembershipType { get; private set; } = null!;

    public DateOnly JoinedOn { get; private set; }

    public MemberStatus Status { get; private set; }

    /// <summary>Why the member was suspended. Null unless suspended.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>
    /// True when this member is permitted to borrow.
    /// </summary>
    /// <remarks>
    /// Phase 4 checks this before issuing. Expressed as a property rather than
    /// scattered <c>Status == Active</c> comparisons so that adding a status
    /// later — or making eligibility depend on unpaid fines — is one edit.
    /// </remarks>
    public bool CanBorrow => Status == MemberStatus.Active;

    public static Member Create(
        string fullName,
        Email email,
        int membershipTypeId,
        DateOnly joinedOn,
        PhoneNumber? phone = null,
        string? address = null)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new BusinessRuleViolationException(
                "member.name_required", "Member name is required.");
        }

        if (membershipTypeId <= 0)
        {
            throw new BusinessRuleViolationException(
                "member.membership_type_required", "A membership type is required.");
        }

        // Guarded here and not only in the validator. The validator produces the
        // per-field message a form shows; this makes the rule hold for the
        // seeder and any future bulk-import path, neither of which passes
        // through a validator. An upper bound cannot live here - the entity has
        // no clock, and reading one would be the DateTime.Now this codebase
        // forbids - so "not in the future" stays a validator rule.
        if (joinedOn < EarliestJoinDate)
        {
            throw new BusinessRuleViolationException(
                "member.join_date_too_early",
                $"Join date cannot be before {EarliestJoinDate:yyyy-MM-dd}.");
        }

        return new Member(fullName.Trim(), email, phone, membershipTypeId, joinedOn, address?.Trim());
    }

    public void UpdateDetails(
        string fullName,
        Email email,
        PhoneNumber? phone,
        string? address,
        int membershipTypeId)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new BusinessRuleViolationException(
                "member.name_required", "Member name is required.");
        }

        if (membershipTypeId <= 0)
        {
            throw new BusinessRuleViolationException(
                "member.membership_type_required", "A membership type is required.");
        }

        FullName = fullName.Trim();
        Email = email.Value;
        Phone = phone?.Value;
        Address = address?.Trim();
        MembershipTypeId = membershipTypeId;
    }

    // -----------------------------------------------------------------------
    // Status transitions
    //
    // Named methods rather than a settable Status property, because each
    // transition has its own rule. A public setter would allow Cancelled ->
    // Active with no record of who decided that or why.
    // -----------------------------------------------------------------------

    /// <summary>Withdraws borrowing privileges, recording why.</summary>
    /// <remarks>
    /// The reason is required. A suspension nobody can explain is one nobody can
    /// fairly lift — and the member is entitled to be told.
    /// </remarks>
    public void Suspend(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleViolationException(
                "member.suspension_reason_required",
                "A reason is required when suspending a member.");
        }

        if (Status == MemberStatus.Cancelled)
        {
            throw new ConflictException(
                "member.cancelled",
                $"Member '{MembershipNumber}' is cancelled and cannot be suspended.");
        }

        Status = MemberStatus.Suspended;
        StatusReason = reason.Trim();
    }

    /// <summary>Restores borrowing privileges.</summary>
    public void Reactivate()
    {
        if (Status == MemberStatus.Active)
        {
            return;   // idempotent: reactivating an active member is not an error
        }

        if (Status == MemberStatus.Cancelled)
        {
            throw new ConflictException(
                "member.cancelled",
                $"Member '{MembershipNumber}' is cancelled. Register a new membership instead.");
        }

        Status = MemberStatus.Active;
        StatusReason = null;
    }

    /// <summary>
    /// Closes the membership at the member's request.
    /// </summary>
    /// <remarks>
    /// Deliberately terminal: <see cref="Reactivate"/> refuses to undo it. A
    /// closed membership that can be quietly reopened is indistinguishable from
    /// one that was never closed, which matters when the closure was a data
    /// protection request.
    /// </remarks>
    /// <exception cref="ConflictException">The membership is already cancelled.</exception>
    public void Cancel(string? reason = null)
    {
        // Refused rather than silently ignored. A second call carries a new
        // reason, and returning 200 while discarding it would tell the caller
        // their text was recorded when the original still stands - the same
        // trap that UnmappedMemberHandling.Disallow exists to close.
        //
        // Reactivate() no-ops on an already-active member rather than throwing,
        // and the difference is deliberate: it takes no payload, so there is no
        // caller intent to discard. StatusReason on a closed membership is a
        // historical record - it may document a data protection request - so it
        // is written once and not revised.
        if (Status == MemberStatus.Cancelled)
        {
            throw new ConflictException(
                "member.already_cancelled",
                $"Member '{MembershipNumber}' is already cancelled.");
        }

        Status = MemberStatus.Cancelled;
        StatusReason = reason?.Trim();
    }

    /// <summary>Marks the membership lapsed through time rather than conduct.</summary>
    /// <remarks>
    /// Refuses a cancelled membership rather than no-opping. Cancelled is
    /// terminal for every transition - Suspend and Reactivate both say so - and
    /// a silent return here would answer 200 to a librarian whose click did
    /// nothing, leaving them to discover the status never moved.
    /// </remarks>
    /// <exception cref="ConflictException">The membership is cancelled.</exception>
    public void Expire()
    {
        if (Status == MemberStatus.Cancelled)
        {
            throw new ConflictException(
                "member.cancelled",
                $"Member '{MembershipNumber}' is cancelled and cannot be expired.");
        }

        Status = MemberStatus.Expired;
        StatusReason = "Membership period elapsed.";
    }
}
