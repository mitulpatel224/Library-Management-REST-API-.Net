using Library.Domain.Exceptions;

namespace Library.Domain.ValueObjects;

/// <summary>
/// A phone number, stored in a normalised digits-only form with an optional
/// leading <c>+</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why normalise.</b> <c>+91 98765 43210</c>, <c>+91-98765-43210</c> and
/// <c>+919876543210</c> are one number typed three ways. Stored verbatim, a
/// librarian searching for a member by phone finds them only if they guess the
/// original punctuation. Separators are stripped on construction so the stored
/// value is canonical and comparison is exact.
/// </para>
/// <para>
/// <b>Why validation stops at length.</b> Numbering plans differ by country and
/// change; E.164 caps the total at 15 digits and sets no universal minimum.
/// Encoding per-country rules here would mean a domain object that rejects
/// legitimate foreign numbers whenever a plan is revised — a maintenance burden
/// that buys very little, since a syntactically valid number still may not ring.
/// </para>
/// <para>
/// So this enforces the E.164 bounds and nothing more. A number that must
/// actually work is proven by sending a message to it.
/// </para>
/// </remarks>
public readonly record struct PhoneNumber
{
    /// <summary>E.164 permits at most 15 digits, excluding the leading <c>+</c>.</summary>
    public const int MaxDigits = 15;

    /// <summary>Shorter than this is a typo rather than a number.</summary>
    public const int MinDigits = 6;

    private PhoneNumber(string value) => Value = value;

    /// <summary>The normalised number: digits only, with an optional leading <c>+</c>.</summary>
    public string Value { get; }

    /// <summary>True when the number carries an international prefix.</summary>
    public bool IsInternational => Value.StartsWith('+');

    public static PhoneNumber Create(string? input)
    {
        if (!TryCreate(input, out PhoneNumber number, out string? error))
        {
            throw new BusinessRuleViolationException("member.invalid_phone", error!);
        }

        return number;
    }

    public static bool TryCreate(string? input, out PhoneNumber number, out string? error)
    {
        number = default;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Phone number is required.";
            return false;
        }

        string trimmed = input.Trim();

        // A '+' is meaningful only as the very first character. Anywhere else it
        // is a typo, and silently dropping it would change the number.
        bool international = trimmed.StartsWith('+');
        string rest = international ? trimmed[1..] : trimmed;

        if (rest.Contains('+', StringComparison.Ordinal))
        {
            error = "A '+' may appear only at the start of a phone number.";
            return false;
        }

        // Spaces, hyphens, brackets and dots are all conventional separators.
        // Anything else is a character that does not belong in a phone number.
        foreach (char c in rest)
        {
            if (!char.IsAsciiDigit(c) && !IsSeparator(c))
            {
                error = "Phone number may contain only digits, spaces, hyphens, dots and brackets.";
                return false;
            }
        }

        string digits = new([.. rest.Where(char.IsAsciiDigit)]);

        if (digits.Length < MinDigits)
        {
            error = $"Phone number must have at least {MinDigits} digits.";
            return false;
        }

        if (digits.Length > MaxDigits)
        {
            error = $"Phone number must have at most {MaxDigits} digits.";
            return false;
        }

        number = new PhoneNumber(international ? $"+{digits}" : digits);
        return true;
    }

    private static bool IsSeparator(char c) =>
        c is ' ' or '-' or '.' or '(' or ')';

    /// <summary>Rehydrates a value already known to be valid, skipping validation.</summary>
    public static PhoneNumber FromTrustedValue(string value) => new(value);

    public override string ToString() => Value;

    public static implicit operator string(PhoneNumber number) => number.Value;
}
