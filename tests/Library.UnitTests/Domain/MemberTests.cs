using Library.Domain.Entities;
using Library.Domain.Enums;
using Library.Domain.Exceptions;
using Library.Domain.ValueObjects;

namespace Library.UnitTests.Domain;

/// <summary>
/// The membership lifecycle, and the invariants that make it defensible.
/// </summary>
/// <remarks>
/// Written after the endpoints were already exercised by hand. Several of these
/// cases exist because hand-testing found the behaviour wrong — the join-date
/// floor and the re-cancel guard in particular — and a test is what stops them
/// regressing silently.
/// </remarks>
public sealed class MemberTests
{
    private static readonly DateOnly JoinedOn = new(2026, 1, 10);

    private static Member NewMember(int membershipTypeId = 1) =>
        Member.Create("Asha Nair", Email.Create("asha@example.com"), membershipTypeId, JoinedOn);

    // ----------------------------------------------------------------- create

    [Fact]
    public void A_new_member_is_active_and_may_borrow()
    {
        Member member = NewMember();

        member.Status.ShouldBe(MemberStatus.Active);
        member.CanBorrow.ShouldBeTrue();
        member.StatusReason.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_rejected(string name)
    {
        Action act = () => Member.Create(
            name, Email.Create("a@b.com"), 1, JoinedOn);

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("member.name_required");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_missing_membership_type_is_rejected(int membershipTypeId)
    {
        Action act = () => NewMember(membershipTypeId);

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("member.membership_type_required");
    }

    /// <remarks>
    /// The join year is baked into the membership number, which is immutable once
    /// issued — so a mis-keyed century prints a card that is wrong forever. This
    /// guard lives in the entity and not only in the validator, because the seeder
    /// and any future import path never pass through a validator.
    /// </remarks>
    [Fact]
    public void A_join_date_before_1900_is_rejected()
    {
        Action act = () => Member.Create(
            "Asha Nair", Email.Create("asha@example.com"), 1, new DateOnly(1899, 12, 31));

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("member.join_date_too_early");
    }

    [Fact]
    public void The_earliest_permitted_join_date_is_accepted()
    {
        Member member = Member.Create(
            "Asha Nair", Email.Create("asha@example.com"), 1, Member.EarliestJoinDate);

        member.JoinedOn.ShouldBe(Member.EarliestJoinDate);
    }

    // ------------------------------------------------------------ transitions

    [Fact]
    public void Suspending_records_the_reason_and_blocks_borrowing()
    {
        Member member = NewMember();

        member.Suspend("Unpaid fines of Rs.250");

        member.Status.ShouldBe(MemberStatus.Suspended);
        member.StatusReason.ShouldBe("Unpaid fines of Rs.250");
        member.CanBorrow.ShouldBeFalse();
    }

    [Fact]
    public void Suspending_without_a_reason_is_rejected()
    {
        Member member = NewMember();

        Action act = () => member.Suspend("   ");

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("member.suspension_reason_required");
    }

    /// <remarks>
    /// Deliberately asymmetric with cancellation. A suspension is reversible and
    /// ongoing, so its grounds can legitimately be revised as fines accumulate.
    /// </remarks>
    [Fact]
    public void Re_suspending_revises_the_reason()
    {
        Member member = NewMember();
        member.Suspend("First reason");

        member.Suspend("Second reason");

        member.StatusReason.ShouldBe("Second reason");
    }

    [Fact]
    public void Reactivating_clears_the_reason()
    {
        Member member = NewMember();
        member.Suspend("Unpaid fines");

        member.Reactivate();

        member.Status.ShouldBe(MemberStatus.Active);
        member.StatusReason.ShouldBeNull();
        member.CanBorrow.ShouldBeTrue();
    }

    /// <remarks>
    /// Idempotent because it carries no payload: returning early discards no
    /// caller intent. Contrast <see cref="Re_cancelling_is_refused"/>.
    /// </remarks>
    [Fact]
    public void Reactivating_an_active_member_is_a_no_op()
    {
        Member member = NewMember();

        Should.NotThrow(member.Reactivate);

        member.Status.ShouldBe(MemberStatus.Active);
    }

    [Fact]
    public void An_expired_membership_can_be_reactivated()
    {
        Member member = NewMember();
        member.Expire();

        member.Reactivate();

        member.Status.ShouldBe(MemberStatus.Active);
    }

    [Fact]
    public void Expiring_blocks_borrowing_and_records_why()
    {
        Member member = NewMember();

        member.Expire();

        member.Status.ShouldBe(MemberStatus.Expired);
        member.CanBorrow.ShouldBeFalse();
        member.StatusReason.ShouldNotBeNullOrWhiteSpace();
    }

    // --------------------------------------------- cancellation is terminal

    [Fact]
    public void Cancelling_records_the_reason_and_is_terminal()
    {
        Member member = NewMember();

        member.Cancel("Member requested closure");

        member.Status.ShouldBe(MemberStatus.Cancelled);
        member.StatusReason.ShouldBe("Member requested closure");
        member.CanBorrow.ShouldBeFalse();
    }

    /// <remarks>
    /// The bug this test pins: a second cancel used to return 200 and overwrite
    /// the closure reason — which may document a data protection request. The call
    /// carries a new reason that cannot be honoured, and answering success while
    /// discarding it would report a revision that never happened.
    /// </remarks>
    [Fact]
    public void Re_cancelling_is_refused()
    {
        Member member = NewMember();
        member.Cancel("Original reason");

        Action act = () => member.Cancel("Overwritten reason");

        act.ShouldThrow<ConflictException>()
            .ErrorCode.ShouldBe("member.already_cancelled");
    }

    [Fact]
    public void The_original_closure_reason_survives_a_refused_re_cancel()
    {
        Member member = NewMember();
        member.Cancel("Original reason");

        Should.Throw<ConflictException>(() => member.Cancel("Overwritten reason"));

        member.StatusReason.ShouldBe("Original reason");
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("reactivate")]
    [InlineData("expire")]
    public void No_transition_escapes_cancellation(string transition)
    {
        Member member = NewMember();
        member.Cancel("Closed");

        Action act = transition switch
        {
            "suspend" => () => member.Suspend("reason"),
            "reactivate" => member.Reactivate,
            _ => member.Expire,
        };

        act.ShouldThrow<ConflictException>()
            .ErrorCode.ShouldBe("member.cancelled");
    }

    // ------------------------------------------------------ membership number

    [Fact]
    public void The_membership_number_carries_the_join_year()
    {
        Member member = NewMember();

        // Simulates the id the database assigns on insert, which the number
        // derives from - hence the two-save registration flow in MemberService.
        typeof(Member).GetProperty(nameof(Member.Id))!.SetValue(member, 42);

        member.AssignMembershipNumber();

        member.MembershipNumber.ShouldBe("MEM-2026-00042");
    }
}
