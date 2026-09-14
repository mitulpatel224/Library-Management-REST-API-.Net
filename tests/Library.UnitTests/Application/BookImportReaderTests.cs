using System.Text;
using Library.Application.Books.Import;
using Library.Infrastructure.Import;

namespace Library.UnitTests.Application;

/// <summary>
/// Verifies the import readers against real file content.
/// </summary>
/// <remarks>
/// These run against a <see cref="MemoryStream"/> with no HTTP and no database
/// — which is the payoff for the readers taking a <see cref="Stream"/> rather
/// than an <c>IFormFile</c>. A reader coupled to ASP.NET Core would need a whole
/// request to test a comma.
/// </remarks>
public sealed class BookImportReaderTests
{
    private static MemoryStream StreamOf(string content) =>
        new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static async Task<List<ImportRow<ImportedBookRow>>> ReadAllAsync(
        IBookImportReader reader, string content)
    {
        List<ImportRow<ImportedBookRow>> rows = [];

        await foreach (ImportRow<ImportedBookRow> row in reader.ReadAsync(StreamOf(content)))
        {
            rows.Add(row);
        }

        return rows;
    }

    // -----------------------------------------------------------------------
    // CSV
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Csv_reads_every_mapped_column()
    {
        const string csv = """
            isbn,title,subtitle,category,publisher,publishedOn,language,pageCount,description,authors,genres
            9780132350884,Clean Code,A Handbook,Software Engineering,Prentice Hall,2008-08-01,en,464,Readable code,Robert C. Martin,Programming;Design
            """;

        List<ImportRow<ImportedBookRow>> rows =
            await ReadAllAsync(new CsvBookImportReader(), csv);

        rows.Count.ShouldBe(1);
        ImportedBookRow row = rows[0].Value!;

        row.Isbn.ShouldBe("9780132350884");
        row.Title.ShouldBe("Clean Code");
        row.Subtitle.ShouldBe("A Handbook");
        row.Category.ShouldBe("Software Engineering");
        row.Publisher.ShouldBe("Prentice Hall");
        row.PublishedOn.ShouldBe("2008-08-01");
        row.PageCount.ShouldBe("464");
        row.Authors.ShouldBe("Robert C. Martin");
        row.Genres.ShouldBe("Programming;Design");
    }

    [Fact]
    public async Task Csv_line_numbers_account_for_the_header()
    {
        // The header is line 1, so the first data row must report 2. Off by one
        // here and every error message points a librarian at the wrong row.
        const string csv = """
            isbn,title,category
            9780132350884,One,Tech
            9780201633610,Two,Tech
            """;

        List<ImportRow<ImportedBookRow>> rows =
            await ReadAllAsync(new CsvBookImportReader(), csv);

        rows[0].LineNumber.ShouldBe(2);
        rows[1].LineNumber.ShouldBe(3);
    }

    [Fact]
    public async Task Csv_accepts_a_file_with_only_the_required_columns()
    {
        // Optional columns are genuinely optional. A narrow file must not throw
        // on the first missing column.
        const string csv = """
            isbn,title,category
            9780132350884,Clean Code,Software Engineering
            """;

        List<ImportRow<ImportedBookRow>> rows =
            await ReadAllAsync(new CsvBookImportReader(), csv);

        rows.Count.ShouldBe(1);
        rows[0].IsReadable.ShouldBeTrue();
        rows[0].Value!.Publisher.ShouldBeNull();
        rows[0].Value!.PageCount.ShouldBeNull();
    }

    [Theory]
    [InlineData("PageCount")]
    [InlineData("pagecount")]
    [InlineData("page_count")]
    [InlineData("  PAGECOUNT  ")]
    public async Task Csv_matches_headers_regardless_of_case_and_underscores(string header)
    {
        // These files come from spreadsheets and other people's exports.
        // Rejecting correct data over header presentation helps nobody.
        string csv = $"isbn,title,category,{header}\n9780132350884,Clean Code,Tech,464";

        List<ImportRow<ImportedBookRow>> rows =
            await ReadAllAsync(new CsvBookImportReader(), csv);

        rows[0].Value!.PageCount.ShouldBe("464");
    }

    [Fact]
    public async Task Csv_of_only_a_header_yields_nothing_rather_than_failing()
    {
        List<ImportRow<ImportedBookRow>> rows =
            await ReadAllAsync(new CsvBookImportReader(), "isbn,title,category\n");

        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_csv_yields_nothing()
    {
        (await ReadAllAsync(new CsvBookImportReader(), string.Empty)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Csv_quoted_field_containing_a_comma_stays_one_field()
    {
        const string csv = """
            isbn,title,category
            9780132350884,"Clean Code, Second Edition",Tech
            """;

        List<ImportRow<ImportedBookRow>> rows =
            await ReadAllAsync(new CsvBookImportReader(), csv);

        rows[0].Value!.Title.ShouldBe("Clean Code, Second Edition");
    }

    // -----------------------------------------------------------------------
    // JSON
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Json_reads_an_array_of_objects()
    {
        const string json = """
            [
              { "isbn": "9780132350884", "title": "Clean Code", "category": "Tech" },
              { "isbn": "9780201633610", "title": "Design Patterns", "category": "Tech" }
            ]
            """;

        List<ImportRow<ImportedBookRow>> rows =
            await ReadAllAsync(new JsonBookImportReader(), json);

        rows.Count.ShouldBe(2);
        rows[0].Value!.Title.ShouldBe("Clean Code");
        rows[1].LineNumber.ShouldBe(2);
    }

    [Fact]
    public async Task Json_ignores_properties_it_does_not_map()
    {
        // Deliberately the OPPOSITE of the API's strict binding. A supplier's
        // file carries their schema, and refusing it over a field we do not need
        // would make the feature unusable.
        const string json = """
            [ { "isbn": "9780132350884", "title": "Clean Code", "category": "Tech",
                "supplierSku": "ABC-123", "warehouse": { "bay": 4 } } ]
            """;

        List<ImportRow<ImportedBookRow>> rows =
            await ReadAllAsync(new JsonBookImportReader(), json);

        rows.Count.ShouldBe(1);
        rows[0].IsReadable.ShouldBeTrue();
        rows[0].Value!.Title.ShouldBe("Clean Code");
    }

    [Fact]
    public async Task Json_matches_property_names_case_insensitively()
    {
        const string json = """
            [ { "ISBN": "9780132350884", "Title": "Clean Code", "CATEGORY": "Tech" } ]
            """;

        List<ImportRow<ImportedBookRow>> rows =
            await ReadAllAsync(new JsonBookImportReader(), json);

        rows[0].Value!.Isbn.ShouldBe("9780132350884");
        rows[0].Value!.Category.ShouldBe("Tech");
    }

    [Fact]
    public async Task Malformed_json_yields_an_error_and_stops()
    {
        // Documents a real difference between the two formats, and one worth
        // knowing before uploading a large file.
        //
        // CSV is line-oriented: a bad row is reported and the reader carries on,
        // so 9,996 good rows still import. JSON is not. DeserializeAsyncEnumerable
        // reads ahead in buffer-sized chunks, so a syntax error ANYWHERE in the
        // document surfaces before the elements preceding it have been yielded -
        // this file's first element is perfectly valid and never arrives.
        //
        // So: a malformed JSON file imports nothing, and the caller must fix the
        // syntax and re-upload. A malformed CSV imports everything except the
        // broken lines.
        const string json = """
            [ { "isbn": "9780132350884", "title": "One", "category": "Tech" },
              { "isbn": BROKEN } ]
            """;

        List<ImportRow<ImportedBookRow>> rows =
            await ReadAllAsync(new JsonBookImportReader(), json);

        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(r => !r.IsReadable);
        rows[^1].ReadError.ShouldNotBeNull();
    }

    [Fact]
    public async Task Valid_json_after_a_valid_element_still_streams_both()
    {
        // The contrast with the test above: with no syntax error, elements are
        // yielded one at a time as the array is consumed.
        const string json = """
            [ { "isbn": "9780132350884", "title": "One", "category": "Tech" },
              { "isbn": "9780201633610", "title": "Two", "category": "Tech" } ]
            """;

        List<ImportRow<ImportedBookRow>> rows =
            await ReadAllAsync(new JsonBookImportReader(), json);

        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.IsReadable);
    }

    [Fact]
    public async Task An_empty_json_array_yields_nothing()
    {
        (await ReadAllAsync(new JsonBookImportReader(), "[]")).ShouldBeEmpty();
    }

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    [Fact]
    public void The_factory_returns_the_reader_declaring_that_format()
    {
        var factory = new BookImportReaderFactory(
            [new CsvBookImportReader(), new JsonBookImportReader()]);

        factory.GetReader(ImportFormat.Csv).ShouldBeOfType<CsvBookImportReader>();
        factory.GetReader(ImportFormat.Json).ShouldBeOfType<JsonBookImportReader>();
    }

    [Fact]
    public void The_factory_rejects_a_format_with_no_registered_reader()
    {
        var factory = new BookImportReaderFactory([new CsvBookImportReader()]);

        Should.Throw<Library.Domain.Exceptions.BusinessRuleViolationException>(
                () => factory.GetReader(ImportFormat.Json))
            .ErrorCode.ShouldBe("import.unsupported_format");
    }
}
