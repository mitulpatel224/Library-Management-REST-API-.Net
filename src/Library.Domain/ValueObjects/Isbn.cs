using Library.Domain.Exceptions;

namespace Library.Domain.ValueObjects;

/// <summary>
/// A validated ISBN-13, stored as 13 digits with no hyphens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a type instead of a string.</b> A <c>string Isbn</c> property can hold
/// "hello", an empty string, or an ISBN with a bad check digit, and nothing stops
/// it. Parsing once at the boundary into a type that cannot exist in an invalid
/// state means every later layer can stop re-checking. This is "make illegal
/// states unrepresentable" applied to the one field the whole catalogue is keyed
/// and searched on.
/// </para>
/// <para>
/// <b>Entity versus value object.</b> A <c>Book</c> is an entity: it has identity,
/// and two books with the same title are still two books. An <c>Isbn</c> is a
/// value: two instances holding 9780132350884 are interchangeable, which is why
/// this is a <c>record struct</c> - equality by value, and no heap allocation.
/// </para>
/// <para>
/// <b>Normalisation on input.</b> "978-0-13-235088-4" and "9780132350884" are the
/// same ISBN. Hyphens are stripped on construction so both spell the same stored
/// value, which is what lets the unique index actually catch duplicates. Storing
/// the display form would let the same book be added twice under two spellings.
/// </para>
/// </remarks>
public readonly record struct Isbn
{
    public const int Length = 13;

    private Isbn(string value) => Value = value;

    /// <summary>The 13 digits, unhyphenated.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses and validates an ISBN-13, throwing if it is not one.
    /// </summary>
    /// <exception cref="BusinessRuleViolationException">The input is not a valid ISBN-13.</exception>
    public static Isbn Create(string? input)
    {
        if (!TryCreate(input, out Isbn isbn, out string? error))
        {
            throw new BusinessRuleViolationException("book.invalid_isbn", error!);
        }

        return isbn;
    }

    /// <summary>
    /// Non-throwing parse, for bulk import where a bad row should be reported
    /// rather than abort the batch.
    /// </summary>
    public static bool TryCreate(string? input, out Isbn isbn, out string? error)
    {
        isbn = default;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "ISBN is required.";
            return false;
        }

        string digits = Normalize(input);

        if (digits.Length != Length)
        {
            error = $"ISBN must be 13 digits; got {digits.Length}.";
            return false;
        }

        if (!IsChecksumValid(digits))
        {
            // The check digit is what distinguishes a real ISBN from 13 arbitrary
            // digits. It catches the common data-entry errors - a mistyped digit
            // and most transpositions - before they reach the database.
            error = "ISBN check digit is invalid.";
            return false;
        }

        isbn = new Isbn(digits);
        return true;
    }

    /// <summary>
    /// Rehydrates a value already known to be valid, skipping validation.
    /// </summary>
    /// <remarks>
    /// Used only by the EF Core value converter when reading a row that was
    /// validated on the way in. Re-validating on every materialisation would
    /// cost a checksum pass per row per query for no benefit.
    /// </remarks>
    public static Isbn FromTrustedValue(string value) => new(value);

    /// <summary>Strips hyphens, spaces, and any other separator.</summary>
    private static string Normalize(string input)
    {
        Span<char> buffer = stackalloc char[input.Length];
        int length = 0;

        foreach (char c in input)
        {
            if (char.IsAsciiDigit(c))
            {
                buffer[length++] = c;
            }
        }

        return new string(buffer[..length]);
    }

    /// <summary>
    /// ISBN-13 checksum: digits alternately weighted 1 and 3 must sum to a
    /// multiple of 10.
    /// </summary>
    private static bool IsChecksumValid(string digits)
    {
        int sum = 0;

        for (int i = 0; i < Length; i++)
        {
            int digit = digits[i] - '0';
            sum += (i % 2 == 0) ? digit : digit * 3;
        }

        return sum % 10 == 0;
    }

    /// <summary>Hyphenated display form, e.g. 978-0-13-235088-4.</summary>
    public string ToDisplayString() =>
        $"{Value[..3]}-{Value[3]}-{Value[4..6]}-{Value[6..12]}-{Value[12]}";

    public override string ToString() => Value;

    public static implicit operator string(Isbn isbn) => isbn.Value;
}
