using Library.Domain.Common;
using Library.Domain.Entities;
using Library.Domain.Enums;
using Library.Domain.Events;
using Library.Domain.Exceptions;
using Library.Domain.ValueObjects;
using Library.UnitTests.Common;

namespace Library.UnitTests.Domain;

/// <summary>
/// Lending, overdue arithmetic, and the rules that keep a fine defensible.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the file <c>IClock</c> exists for.</b> Every assertion below about
/// "16 days overdue" would otherwise need either a 16-day wait or a back-dated
/// row that quietly assumes the arithmetic is symmetric — and it is that
/// assumption, not the wait, that hides real bugs.
/// </para>
/// <para>
/// The entity takes the instant as a parameter rather than holding a clock,
/// because <c>Library.Domain</c> references nothing and cannot be given one.
/// </para>
/// </remarks>
public sealed class LoanTests
{
    private static readonly DateTimeOffset Now = FakeClock.DefaultNow;

    private static BookCopy NewCopy() =>
        BookCopy.Create(bookId: 1, barcode: "LIB-000001", condition: CopyCondition.Good);

    private static Member NewMember(MemberStatus status = MemberStatus.Active)
    {
        Member member = Member.Create(
            "Asha Nair", Email.Create("asha@example.com"), 1, new DateOnly(2026, 1, 1));

        if (status == MemberStatus.Suspended)
        {
            member.Suspend("Unpaid fines");
        }
        else if (status == MemberStatus.Cancelled)
        {
            member.Cancel("Closed");
        }
        else if (status == MemberStatus.Expired)
        {
            member.Expire();
        }

        return member;
    }

    private static Loan NewLoan(int loanPeriodDays = 14) =>
        Loan.Issue(NewCopy(), NewMember(), Now, loanPeriodDays);

    // ------------------------------------------------------------------ issue

    [Fact]
    public void Issuing_sets_the_due_date_from_the_loan_period()
    {
        Loan loan = NewLoan(loanPeriodDays: 14);

        loan.IssuedAt.ShouldBe(Now);
        loan.DueAt.ShouldBe(Now.AddDays(14));
        loan.IsReturned.ShouldBeFalse();
    }

    [Fact]
    public void Issuing_marks_the_copy_on_loan()
    {
        BookCopy copy = NewCopy();

        Loan.Issue(copy, NewMember(), Now, 14);

        copy.Status.ShouldBe(CopyStatus.OnLoan);
        copy.IsAvailable.ShouldBeFalse();
    }

    /// <remarks>
    /// The entity check, not the index. This produces the readable error; the
    /// filtered unique index is what makes the rule hold under concurrency.
    /// </remarks>
    [Fact]
    public void A_copy_already_on_loan_cannot_be_issued_again()
    {
        BookCopy copy = NewCopy();
        Loan.Issue(copy, NewMember(), Now, 14);

        Action act = () => Loan.Issue(copy, NewMember(), Now, 14);

        act.ShouldThrow<ConflictException>()
            .ErrorCode.ShouldBe("copy.not_available");
    }

    [Theory]
    [InlineData(MemberStatus.Suspended)]
    [InlineData(MemberStatus.Cancelled)]
    [InlineData(MemberStatus.Expired)]
    public void A_member_who_may_not_borrow_is_refused(MemberStatus status)
    {
        Action act = () => Loan.Issue(NewCopy(), NewMember(status), Now, 14);

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("member.cannot_borrow");
    }

    [Fact]
    public void A_refused_issue_leaves_the_copy_available()
    {
        BookCopy copy = NewCopy();

        Should.Throw<BusinessRuleViolationException>(
            () => Loan.Issue(copy, NewMember(MemberStatus.Suspended), Now, 14));

        // The borrow check runs BEFORE MarkOnLoan, so a refused issue does not
        // strand the copy in OnLoan with no loan pointing at it.
        copy.Status.ShouldBe(CopyStatus.Available);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_non_positive_loan_period_is_rejected(int days)
    {
        Action act = () => Loan.Issue(NewCopy(), NewMember(), Now, days);

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("loan.invalid_period");
    }

    // -------------------------------------------------- overdue arithmetic

    [Fact]
    public void A_loan_within_its_period_is_active_and_not_overdue()
    {
        Loan loan = NewLoan();

        loan.StatusAt(Now.AddDays(13)).ShouldBe(LoanStatus.Active);
        loan.DaysOverdueAt(Now.AddDays(13)).ShouldBe(0);
    }

    /// <remarks>
    /// Whole days only. Charging fifty rupees for being an hour late is not a rule
    /// a librarian wants to defend at the desk.
    /// </remarks>
    [Fact]
    public void An_hour_late_is_not_yet_a_day_overdue()
    {
        Loan loan = NewLoan();

        loan.StatusAt(Now.AddDays(14).AddHours(1)).ShouldBe(LoanStatus.Overdue);
        loan.DaysOverdueAt(Now.AddDays(14).AddHours(1)).ShouldBe(0);
    }

    [Theory]
    [InlineData(15, 1)]
    [InlineData(30, 16)]
    [InlineData(75, 61)]
    public void Days_overdue_counts_from_the_due_date(int daysAfterIssue, int expectedOverdue)
    {
        Loan loan = NewLoan(loanPeriodDays: 14);

        loan.DaysOverdueAt(Now.AddDays(daysAfterIssue)).ShouldBe(expectedOverdue);
    }

    /// <remarks>
    /// Once returned the span is measured to the return, not to now — otherwise a
    /// fine for a book returned last year would keep growing forever.
    /// </remarks>
    [Fact]
    public void Days_overdue_stops_accruing_once_returned()
    {
        Loan loan = NewLoan();
        loan.Return(Now.AddDays(20));   // 6 days late

        loan.DaysOverdueAt(Now.AddDays(400)).ShouldBe(6);
    }

    // ----------------------------------------------------------------- return

    [Fact]
    public void Returning_closes_the_loan_and_shelves_the_copy()
    {
        BookCopy copy = NewCopy();
        Loan loan = Loan.Issue(copy, NewMember(), Now, 14);

        loan.Return(Now.AddDays(5));

        loan.IsReturned.ShouldBeTrue();
        loan.StatusAt(Now.AddDays(5)).ShouldBe(LoanStatus.Returned);
        copy.Status.ShouldBe(CopyStatus.Available);
    }

    [Fact]
    public void A_copy_returned_in_poor_condition_does_not_go_back_on_the_shelf()
    {
        BookCopy copy = NewCopy();
        Loan loan = Loan.Issue(copy, NewMember(), Now, 14);

        loan.Return(Now.AddDays(5), CopyCondition.Poor);

        copy.Status.ShouldBe(CopyStatus.Damaged);
    }

    [Fact]
    public void Returning_twice_is_refused()
    {
        Loan loan = NewLoan();
        loan.Return(Now.AddDays(5));

        Action act = () => loan.Return(Now.AddDays(6));

        act.ShouldThrow<ConflictException>()
            .ErrorCode.ShouldBe("loan.already_returned");
    }

    [Fact]
    public void Returning_before_the_issue_date_is_refused()
    {
        Loan loan = NewLoan();

        Action act = () => loan.Return(Now.AddDays(-1));

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("loan.return_before_issue");
    }

    // ------------------------------------------------------ the domain event

    [Fact]
    public void Returning_raises_a_LoanReturnedEvent_carrying_the_overdue_span()
    {
        Loan loan = NewLoan(loanPeriodDays: 14);

        loan.Return(Now.AddDays(30));

        LoanReturnedEvent raised = loan.DomainEvents
            .OfType<LoanReturnedEvent>()
            .ShouldHaveSingleItem();

        raised.DaysOverdue.ShouldBe(16);
        raised.IsLate.ShouldBeTrue();
        raised.ReturnedAt.ShouldBe(Now.AddDays(30));
    }

    /// <remarks>
    /// The event is raised for an on-time return too. Deciding there is nothing to
    /// charge is the handler's job, not the entity's — the entity reports what
    /// happened, and the consequence is chosen elsewhere.
    /// </remarks>
    [Fact]
    public void An_on_time_return_raises_the_event_but_is_not_late()
    {
        Loan loan = NewLoan();

        loan.Return(Now.AddDays(5));

        LoanReturnedEvent raised = loan.DomainEvents
            .OfType<LoanReturnedEvent>()
            .ShouldHaveSingleItem();

        raised.DaysOverdue.ShouldBe(0);
        raised.IsLate.ShouldBeFalse();
    }

    /// <remarks>
    /// Nothing is dispatched by the entity. It collects, and the infrastructure
    /// dispatches once the transaction has committed — which is the only reason a
    /// rolled-back return cannot leave a fine behind.
    /// </remarks>
    [Fact]
    public void Issuing_raises_no_events()
    {
        Loan loan = NewLoan();

        loan.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Clearing_events_empties_the_collection()
    {
        Loan loan = NewLoan();
        loan.Return(Now.AddDays(5));

        ((IHasDomainEvents)loan).ClearDomainEvents();

        loan.DomainEvents.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ renew

    [Fact]
    public void Renewing_extends_the_due_date()
    {
        Loan loan = NewLoan(loanPeriodDays: 14);

        loan.Renew(Now.AddDays(3), additionalDays: 14);

        loan.DueAt.ShouldBe(Now.AddDays(28));
    }

    /// <remarks>
    /// Refused once overdue: renewing a late loan would erase a fine that has
    /// already accrued, turning "return it late and renew" into a way of never
    /// paying.
    /// </remarks>
    [Fact]
    public void An_overdue_loan_cannot_be_renewed()
    {
        Loan loan = NewLoan(loanPeriodDays: 14);

        Action act = () => loan.Renew(Now.AddDays(20), additionalDays: 14);

        act.ShouldThrow<ConflictException>()
            .ErrorCode.ShouldBe("loan.overdue_cannot_renew");
    }

    [Fact]
    public void A_closed_loan_cannot_be_renewed()
    {
        Loan loan = NewLoan();
        loan.Return(Now.AddDays(5));

        Action act = () => loan.Renew(Now.AddDays(6), additionalDays: 14);

        act.ShouldThrow<ConflictException>()
            .ErrorCode.ShouldBe("loan.already_returned");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void A_non_positive_renewal_is_rejected(int days)
    {
        Loan loan = NewLoan();

        Action act = () => loan.Renew(Now.AddDays(1), days);

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("loan.invalid_period");
    }
}
