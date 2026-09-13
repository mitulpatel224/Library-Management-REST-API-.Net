using Library.Application.Common.Abstractions;
using Library.Domain.Entities;
using Library.Domain.Events;
using Library.Domain.Exceptions;

namespace Library.Application.Loans.Handlers;

/// <summary>
/// Assesses a fine when a copy comes back late.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a handler and not three lines inside <c>ReturnAsync</c>.</b>
/// Assessing a fine is a <i>consequence</i> of a return, not part of it. Written
/// inline, the return use case would own a rule that has nothing to do with taking
/// a book back, and the next consequence — notify the member, clear a reservation
/// hold — would be appended to the same method until it did five unrelated things.
/// </para>
/// <para>
/// The cost is real and worth naming: the fine is written in a <b>separate
/// transaction</b>, after the return has committed. There is a window in which a
/// copy is returned and no fine exists yet, and if this handler throws, that
/// window never closes on its own. That is the trade the dispatcher documents —
/// a late fine beats a lost return.
/// </para>
/// </remarks>
public sealed class FineAssessmentHandler : IDomainEventHandler<LoanReturnedEvent>
{
    private readonly IFineRepository _fineRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly FineRateResolver _rateResolver;

    public FineAssessmentHandler(
        IFineRepository fineRepository,
        IUnitOfWork unitOfWork,
        FineRateResolver rateResolver)
    {
        _fineRepository = fineRepository;
        _unitOfWork = unitOfWork;
        _rateResolver = rateResolver;
    }

    public async Task HandleAsync(
        LoanReturnedEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // On time. Nothing owed, nothing written - a zero-amount Fine row would
        // mean "this member was fined nothing", which reads as a charge in every
        // report that counts fines.
        if (!domainEvent.IsLate)
        {
            return;
        }

        // Guards against a replayed event. The unique index on Fine.LoanId is the
        // real protection - this check only turns a double dispatch into a no-op
        // rather than a logged constraint violation.
        if (await _fineRepository.ExistsForLoanAsync(domainEvent.LoanId, cancellationToken))
        {
            return;
        }

        // The rate is resolved NOW, at assessment, and then stored on the row.
        // Fine.RatePerDay is a record of what was charged, not a live lookup: a
        // rate change next year must not silently restate last year's fines.
        decimal ratePerDay = _rateResolver();

        Fine fine = Fine.Assess(
            domainEvent.LoanId,
            domainEvent.MemberId,
            domainEvent.DaysOverdue,
            ratePerDay);

        _fineRepository.Add(fine);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConflictException)
        {
            // Lost a race with a concurrent dispatch of the same event. The other
            // one wrote the fine, which is the outcome we wanted; there is nothing
            // to repair and nothing to report.
        }
    }
}
