using System.ComponentModel.DataAnnotations;

namespace Library.Application.Loans;

/// <summary>
/// Strongly-typed binding of the <c>Fines</c> section of configuration.
/// </summary>
/// <remarks>
/// <para>
/// The rate is ₹50 per overdue day today, and everyone involved expects that to
/// change. Putting it in configuration rather than in a <c>const</c> on
/// <c>Fine</c> is the difference between "edit appsettings and restart" and
/// "change code, review, build, redeploy" when the library revises its schedule.
/// </para>
/// <para>
/// It stops short of a database table, deliberately. A librarian-editable rate
/// needs an audit trail, an effective-from date, and a rule for which rate applies
/// to a loan that spans a change — none of which is in scope here, and all of
/// which would be half-built if the value merely moved into a row. Configuration
/// is honest about the level of flexibility actually provided.
/// </para>
/// </remarks>
public sealed class FineOptions
{
    public const string SectionName = "Fines";

    /// <summary>Currency charged for each whole day a copy is late.</summary>
    [Range(0, 10_000)]
    public decimal RatePerDay { get; set; } = 50m;

    /// <summary>ISO 4217 code, used only for display.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Currency { get; set; } = "INR";
}

/// <summary>
/// Supplies the fine rate in force when a fine is assessed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a delegate rather than injecting <c>IOptions&lt;FineOptions&gt;</c>
/// into the handler.</b> The handler needs one number, not a configuration
/// object. Depending on the delegate means it cannot read
/// <c>FineOptions.Currency</c>, cannot be broken by a field added to that class,
/// and can be tested by passing <c>() =&gt; 50m</c> — no options plumbing, no
/// mock.
/// </para>
/// <para>
/// It is also the seam that makes a future per-membership-type or
/// effective-dated rate a change in one registration rather than a change in
/// every caller. Open/Closed applied to the one business rule this project
/// already knows is going to move.
/// </para>
/// <para>
/// <b>Why <c>Library.Application</c> and not <c>Library.Domain</c>.</b> The domain
/// references nothing, so it cannot hold a delegate bound to configuration.
/// <c>Fine.Assess</c> takes the rate as a plain parameter and stays ignorant of
/// where it came from.
/// </para>
/// </remarks>
/// <returns>The rate per overdue day, in <see cref="FineOptions.Currency"/>.</returns>
public delegate decimal FineRateResolver();

/// <summary>
/// Supplies the currency code fine amounts are expressed in.
/// </summary>
/// <remarks>
/// <para>
/// A second delegate rather than widening <see cref="FineRateResolver"/> to
/// return a pair. The fine handler needs only the rate and should not be handed
/// a currency it has no use for; the fine <i>report</i> needs only the currency
/// and never the rate. Two narrow dependencies say more about what each caller
/// actually uses than one wide one.
/// </para>
/// <para>
/// It exists at all because <c>Library.Application</c> has no configuration
/// reference — that was removed once nothing needed it — so a report here cannot
/// read <c>IOptions&lt;FineOptions&gt;</c>. Infrastructure binds the options and
/// supplies this delegate, which is the same inversion <see cref="FineRateResolver"/>
/// uses.
/// </para>
/// </remarks>
/// <returns>An ISO 4217 code, for display only.</returns>
public delegate string FineCurrencyResolver();
