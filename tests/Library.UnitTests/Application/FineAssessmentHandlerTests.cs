using Library.Application.Common.Abstractions;
using Library.Application.Loans;
using Library.Application.Loans.Handlers;
using Library.Domain.Entities;
using Library.Domain.Events;
using NSubstitute;

namespace Library.UnitTests.Application;

/// <summary>
/// The handler that turns a late return into money owed.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the <c>FineRateResolver</c> delegate buys, demonstrated.</b> Every test
/// here supplies the rate as <c>() =&gt; 50m</c> — no options binding, no
/// configuration, no mock of <c>IOptions&lt;FineOptions&gt;</c>. Depending on a
/// one-method delegate rather than a settings object is what makes that possible.
/// </para>
/// <para>
/// The repository and unit of work are substituted because this test is about the
/// handler's <i>decisions</i> — assess or not, at what rate — and not about
/// whether EF Core can insert a row.
/// </para>
/// </remarks>
public sealed class FineAssessmentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly IFineRepository _fines = Substitute.For<IFineRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private FineAssessmentHandler CreateHandler(decimal rate = 50m) =>
        new(_fines, _unitOfWork, () => rate);

    private static LoanReturnedEvent ReturnedEvent(int daysOverdue) => new(
        LoanId: 1,
        MemberId: 7,
        BookCopyId: 3,
        DueAt: Now.AddDays(-daysOverdue),
        ReturnedAt: Now,
        DaysOverdue: daysOverdue);

    [Fact]
    public async Task A_late_return_is_fined_at_the_resolved_rate()
    {
        _fines.ExistsForLoanAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        await CreateHandler(rate: 50m).HandleAsync(ReturnedEvent(daysOverdue: 16), TestContext.Current.CancellationToken);

        _fines.Received(1).Add(Arg.Is<Fine>(f =>
            f.LoanId == 1 &&
            f.MemberId == 7 &&
            f.DaysOverdue == 16 &&
            f.RatePerDay == 50m &&
            f.Amount == 800m));

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <remarks>
    /// Proof the rate is not hard-coded anywhere in the assessment path: change the
    /// delegate, and the amount changes with it.
    /// </remarks>
    [Fact]
    public async Task Changing_the_rate_changes_the_amount()
    {
        _fines.ExistsForLoanAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        await CreateHandler(rate: 125m).HandleAsync(ReturnedEvent(daysOverdue: 4), TestContext.Current.CancellationToken);

        _fines.Received(1).Add(Arg.Is<Fine>(f => f.RatePerDay == 125m && f.Amount == 500m));
    }

    /// <remarks>
    /// No row is written for an on-time return. A zero-amount fine would read as a
    /// charge in every report that counts fines, and would have to be excluded by
    /// hand everywhere.
    /// </remarks>
    [Fact]
    public async Task An_on_time_return_is_not_fined()
    {
        await CreateHandler().HandleAsync(ReturnedEvent(daysOverdue: 0), TestContext.Current.CancellationToken);

        _fines.DidNotReceive().Add(Arg.Any<Fine>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_on_time_return_does_not_even_query_for_an_existing_fine()
    {
        await CreateHandler().HandleAsync(ReturnedEvent(daysOverdue: 0), TestContext.Current.CancellationToken);

        await _fines.DidNotReceive()
            .ExistsForLoanAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <remarks>
    /// Guards a replayed event. The unique index on <c>Fine.LoanId</c> is the real
    /// protection; this turns a double dispatch into a quiet no-op rather than a
    /// logged constraint violation.
    /// </remarks>
    [Fact]
    public async Task A_loan_that_already_has_a_fine_is_not_fined_twice()
    {
        _fines.ExistsForLoanAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        await CreateHandler().HandleAsync(ReturnedEvent(daysOverdue: 16), TestContext.Current.CancellationToken);

        _fines.DidNotReceive().Add(Arg.Any<Fine>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_cap_applies_through_the_handler_too()
    {
        _fines.ExistsForLoanAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        await CreateHandler(rate: 50m).HandleAsync(ReturnedEvent(daysOverdue: 1_000), TestContext.Current.CancellationToken);

        _fines.Received(1).Add(Arg.Is<Fine>(f => f.Amount == Fine.MaxAmount));
    }
}
