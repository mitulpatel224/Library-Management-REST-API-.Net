using Library.Application.Common.Abstractions;

namespace Library.UnitTests.Common;

/// <summary>
/// A clock the test controls.
/// </summary>
/// <remarks>
/// <para>
/// This is the payoff for injecting <see cref="IClock"/> everywhere instead of
/// calling <c>DateTimeOffset.UtcNow</c>. Testing "a loan 12 days overdue accrues
/// a 600 rupee fine" becomes three lines and runs in a millisecond:
/// </para>
/// <code>
/// var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
/// var loan  = Loan.Issue(copy, member, clock.UtcNow, loanPeriodDays: 14);
/// clock.Advance(TimeSpan.FromDays(26));      // 12 days past the due date
/// </code>
/// <para>
/// Without it the same test needs either a 12-day wait or a back-dated loan that
/// quietly assumes the arithmetic is symmetric - and it is that assumption, not
/// the wait, that hides real bugs.
/// </para>
/// </remarks>
public sealed class FakeClock : IClock
{
    /// <summary>A fixed, arbitrary instant used when a test does not care about the date.</summary>
    public static readonly DateTimeOffset DefaultNow =
        new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    public FakeClock(DateTimeOffset? now = null) => UtcNow = now ?? DefaultNow;

    public DateTimeOffset UtcNow { get; private set; }

    public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);

    /// <summary>Moves the clock forward. Negative spans are allowed, for testing the past.</summary>
    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);

    /// <summary>Convenience for the common "n days later" case.</summary>
    public void AdvanceDays(int days) => Advance(TimeSpan.FromDays(days));

    /// <summary>Jumps to a specific instant.</summary>
    public void SetTo(DateTimeOffset instant) => UtcNow = instant;
}
