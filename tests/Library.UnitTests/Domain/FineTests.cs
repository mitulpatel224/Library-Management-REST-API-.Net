using Library.Domain.Entities;
using Library.Domain.Exceptions;
using Library.UnitTests.Common;

namespace Library.UnitTests.Domain;

/// <summary>
/// Fine arithmetic and settlement.
/// </summary>
/// <remarks>
/// The rate arrives as a parameter, never as a constant on this type. That is
/// what lets the library change its schedule in configuration without touching
/// the domain — and it is why these tests can state the rate they expect instead
/// of importing one.
/// </remarks>
public sealed class FineTests
{
    private static readonly DateTimeOffset Now = FakeClock.DefaultNow;

    private const decimal Rate = 50m;

    private static Fine NewFine(int daysOverdue = 16, decimal ratePerDay = Rate) =>
        Fine.Assess(loanId: 1, memberId: 1, daysOverdue, ratePerDay);

    // --------------------------------------------------------------- assess

    [Theory]
    [InlineData(1, 50)]
    [InlineData(16, 800)]
    [InlineData(61, 3050)]
    public void The_amount_is_days_times_rate(int daysOverdue, decimal expected)
    {
        Fine fine = NewFine(daysOverdue);

        fine.Amount.ShouldBe(expected);
        fine.DaysOverdue.ShouldBe(daysOverdue);
    }

    /// <remarks>
    /// The rate is captured on the row rather than looked up later. A fine is a
    /// record of what was charged; changing the schedule next year must not
    /// silently restate last year's fines.
    /// </remarks>
    [Fact]
    public void The_rate_applied_is_recorded_on_the_fine()
    {
        Fine fine = NewFine(daysOverdue: 10, ratePerDay: 75m);

        fine.RatePerDay.ShouldBe(75m);
        fine.Amount.ShouldBe(750m);
    }

    /// <remarks>
    /// A copy forgotten for three years would otherwise accrue an uncollectable
    /// five-figure charge nobody will pay and everybody has to argue about. At the
    /// cap the conversation becomes "replace the book", which is the right one.
    /// </remarks>
    [Fact]
    public void The_amount_is_capped()
    {
        Fine fine = NewFine(daysOverdue: 1_000);

        fine.Amount.ShouldBe(Fine.MaxAmount);
    }

    [Fact]
    public void Just_below_the_cap_is_not_capped()
    {
        Fine fine = NewFine(daysOverdue: 99);   // 99 x 50 = 4950

        fine.Amount.ShouldBe(4950m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_fine_cannot_be_assessed_for_an_on_time_return(int daysOverdue)
    {
        Action act = () => NewFine(daysOverdue);

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("fine.not_overdue");
    }

    [Fact]
    public void A_negative_rate_is_rejected()
    {
        Action act = () => NewFine(ratePerDay: -1m);

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("fine.invalid_rate");
    }

    [Fact]
    public void A_new_fine_is_outstanding()
    {
        Fine fine = NewFine();

        fine.IsSettled.ShouldBeFalse();
        fine.OutstandingAmount.ShouldBe(800m);
    }

    // ------------------------------------------------------------ settlement

    [Fact]
    public void Paying_settles_the_fine_and_clears_the_outstanding_amount()
    {
        Fine fine = NewFine();

        fine.Pay(Now);

        fine.PaidAt.ShouldBe(Now);
        fine.IsSettled.ShouldBeTrue();

        // The amount is history and does not move; only what is owed changes.
        fine.Amount.ShouldBe(800m);
        fine.OutstandingAmount.ShouldBe(0m);
    }

    [Fact]
    public void Paying_twice_is_refused()
    {
        Fine fine = NewFine();
        fine.Pay(Now);

        Action act = () => fine.Pay(Now.AddMinutes(1));

        act.ShouldThrow<ConflictException>()
            .ErrorCode.ShouldBe("fine.already_paid");
    }

    [Fact]
    public void Waiving_records_the_reason_and_settles_the_fine()
    {
        Fine fine = NewFine();

        fine.Waive(Now, "Hospitalised; produced documentation");

        fine.WaivedAt.ShouldBe(Now);
        fine.WaivedReason.ShouldBe("Hospitalised; produced documentation");
        fine.OutstandingAmount.ShouldBe(0m);
    }

    /// <remarks>
    /// Required by the entity, not merely by the validator: waiving money owed is
    /// precisely the operation that has to be reviewable afterwards.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Waiving_without_a_reason_is_refused(string reason)
    {
        Fine fine = NewFine();

        Action act = () => fine.Waive(Now, reason);

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("fine.waiver_reason_required");
    }

    [Fact]
    public void A_paid_fine_cannot_be_waived()
    {
        Fine fine = NewFine();
        fine.Pay(Now);

        Action act = () => fine.Waive(Now, "goodwill");

        act.ShouldThrow<ConflictException>()
            .ErrorCode.ShouldBe("fine.already_paid");
    }

    [Fact]
    public void A_waived_fine_cannot_be_paid()
    {
        Fine fine = NewFine();
        fine.Waive(Now, "goodwill");

        Action act = () => fine.Pay(Now);

        act.ShouldThrow<ConflictException>()
            .ErrorCode.ShouldBe("fine.already_waived");
    }
}
