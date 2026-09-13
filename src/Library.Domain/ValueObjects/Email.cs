using Library.Domain.Exceptions;

namespace Library.Domain.ValueObjects;

/// <summary>
/// A validated, normalised email address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Normalisation is the point, not just validation.</b>
/// <c>Mitul.Patel@Example.COM</c> and <c>mitul.patel@example.com</c> are the same
/// mailbox. Stored as typed, the unique index would happily accept both and the
/// library would end up with two member records for one person — which then
/// diverge. Lower-casing on construction is what makes
/// <c>IX_Members_Email UNIQUE</c> mean what it appears to mean.
/// </para>
/// <para>
/// <b>On the deliberately loose validation.</b> This checks structure, not
/// correctness: one <c>@</c>, something before it, a dot-bearing domain after it,
/// no whitespace. It does not attempt RFC 5322, and that is a decision rather
/// than laziness:
/// </para>
/// <list type="bullet">
///   <item>The full grammar permits quoted strings, comments and bang paths that
///     no real mailbox uses, so implementing it accepts more garbage, not less.</item>
///   <item>The canonical RFC 5322 regex is about 6,400 characters long and no
///     reviewer can verify it.</item>
///   <item>A syntactically perfect address still may not exist. The only real
///     proof is sending mail to it — which is what a confirmation link is for.</item>
/// </list>
/// <para>
/// So this rejects the typos worth catching cheaply (missing <c>@</c>, trailing
/// space, no domain) and leaves genuine verification to a channel that can
/// actually establish it.
/// </para>
/// </remarks>
public readonly record struct Email
{
    /// <summary>Matches the column width in <c>MemberConfiguration</c>.</summary>
    public const int MaxLength = 256;

    private Email(string value) => Value = value;

    /// <summary>The normalised address: trimmed and lower-cased.</summary>
    public string Value { get; }

    /// <summary>Parses and validates an address, throwing if it is not one.</summary>
    /// <exception cref="BusinessRuleViolationException">The input is not a usable address.</exception>
    public static Email Create(string? input)
    {
        if (!TryCreate(input, out Email email, out string? error))
        {
            throw new BusinessRuleViolationException("member.invalid_email", error!);
        }

        return email;
    }

    /// <summary>
    /// Non-throwing parse, for bulk import where a bad row should be reported
    /// rather than abort the batch.
    /// </summary>
    public static bool TryCreate(string? input, out Email email, out string? error)
    {
        email = default;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Email address is required.";
            return false;
        }

        string normalized = input.Trim().ToLowerInvariant();

        if (normalized.Length > MaxLength)
        {
            error = $"Email address must be {MaxLength} characters or fewer.";
            return false;
        }

        if (normalized.Any(char.IsWhiteSpace))
        {
            error = "Email address cannot contain spaces.";
            return false;
        }

        int at = normalized.IndexOf('@', StringComparison.Ordinal);

        // LastIndexOf differing from IndexOf means there is more than one '@'.
        if (at <= 0 || at != normalized.LastIndexOf('@'))
        {
            error = "Email address must contain exactly one '@'.";
            return false;
        }

        string domain = normalized[(at + 1)..];

        // A domain needs a dot and something either side of it: "user@localhost"
        // is valid on a mail server but never what a member meant to type.
        int dot = domain.IndexOf('.', StringComparison.Ordinal);

        if (dot <= 0 || dot == domain.Length - 1)
        {
            error = "Email address must have a valid domain.";
            return false;
        }

        email = new Email(normalized);
        return true;
    }

    /// <summary>
    /// Rehydrates a value already known to be valid, skipping validation.
    /// </summary>
    /// <remarks>
    /// Used only when reading a row that was validated on the way in.
    /// Re-validating every materialisation would cost a parse per row per query
    /// for no benefit.
    /// </remarks>
    public static Email FromTrustedValue(string value) => new(value);

    public override string ToString() => Value;

    public static implicit operator string(Email email) => email.Value;
}
