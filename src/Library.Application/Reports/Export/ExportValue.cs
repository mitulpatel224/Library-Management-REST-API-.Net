namespace Library.Application.Reports.Export;

/// <summary>
/// One exported cell, carrying whether it must survive as text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cell needs more than its string.</b> Every value reaching an exporter
/// is already a <see cref="string"/> — the row formatted it. But CSV has no
/// types, so the spreadsheet that opens the file guesses, and it guesses from the
/// characters alone. <c>9780132350884</c> is thirteen digits, so Excel reads it as
/// a number and displays <c>9.78E+12</c>. The ISBN is still intact in the file;
/// what the librarian sees is not.
/// </para>
/// <para>
/// The encoder cannot tell an ISBN from a page count once both are strings, and
/// guessing from length would be a rule that quietly breaks the first time a
/// genuine number grows. So the row says which of its columns are identifiers
/// rather than quantities, and the encoder acts on the declaration.
/// </para>
/// <para>
/// <b>Why a struct with an implicit conversion.</b> Most columns are ordinary —
/// requiring every row to wrap every value would be ceremony for the 90% case.
/// The implicit conversion means a row writes <c>Title</c> and gets
/// <see cref="Auto"/>; only the identifier columns spell out
/// <see cref="Text(string?)"/>. Declaring the exception, not the rule.
/// </para>
/// </remarks>
public readonly record struct ExportValue
{
    private ExportValue(string? value, bool isText)
    {
        Value = value;
        IsText = isText;
    }

    /// <summary>The formatted cell content.</summary>
    public string? Value { get; }

    /// <summary>
    /// True when this column is an identifier that must not be read as a number.
    /// </summary>
    /// <remarks>
    /// A declaration of intent, not an instruction. The CSV encoder decides
    /// whether anything needs doing — a barcode like <c>LIB-001000</c> is text
    /// already as far as a spreadsheet is concerned, so marking it costs nothing
    /// and changes nothing. JSON ignores this entirely: every value is written
    /// as a JSON string, so the ambiguity never arises.
    /// </remarks>
    public bool IsText { get; }

    /// <summary>A value the consumer may interpret however it likes.</summary>
    public static ExportValue Auto(string? value) => new(value, isText: false);

    /// <summary>A value that must reach the reader as text, digits and all.</summary>
    public static ExportValue Text(string? value) => new(value, isText: true);

    /// <summary>Named alternate for the implicit conversion, per CA2225.</summary>
    public static ExportValue FromString(string? value) => Auto(value);

    /// <summary>Ordinary strings become <see cref="Auto"/> cells.</summary>
    public static implicit operator ExportValue(string? value) => Auto(value);

    public override string ToString() => Value ?? string.Empty;
}
