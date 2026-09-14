using Library.Application.Books.Import;
using Library.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Library.Api.Controllers;

/// <summary>Bulk import of books from a CSV or JSON file.</summary>
[ApiController]
[Route("api/books/import")]
public sealed class BookImportController : ControllerBase
{
    /// <summary>
    /// Largest upload accepted, in bytes.
    /// </summary>
    /// <remarks>
    /// A limit is a security control, not a convenience. Without one, a single
    /// request can exhaust disk or memory — and the framework's own default
    /// (~28 MB for a multipart body) is not obviously right for this endpoint in
    /// either direction. 20 MB is roughly 100,000 book rows, which is far more
    /// than a realistic feed.
    /// </remarks>
    public const long MaxUploadBytes = 20 * 1024 * 1024;

    private static readonly string[] AllowedCsvExtensions = [".csv", ".txt"];
    private static readonly string[] AllowedJsonExtensions = [".json"];

    private readonly IBookImportService _importService;

    public BookImportController(IBookImportService importService) =>
        _importService = importService;

    /// <summary>Imports books from an uploaded CSV or JSON file.</summary>
    /// <remarks>
    /// <para>
    /// The format is taken from the file extension: <c>.csv</c> or <c>.txt</c>
    /// for CSV, <c>.json</c> for JSON.
    /// </para>
    /// <para>
    /// <b>Partial success is normal and returns 200.</b> A file with 9,996 good
    /// rows and 4 bad ones imports the 9,996 and lists the 4, each with its line
    /// number and a reason. The request succeeded; the rejected rows are data,
    /// not an error in the upload. A 4xx would claim the file was wrong when only
    /// part of it was.
    /// </para>
    /// <para>
    /// Lookups referenced by name — category, publisher, authors, genres — are
    /// created when absent, and the count is reported. An unexpectedly high
    /// <c>lookupsCreated</c> is the signal that a column is mis-mapped or the
    /// file is full of typos.
    /// </para>
    /// <para>
    /// Expected CSV header:
    /// <c>isbn,title,subtitle,category,publisher,publishedOn,language,pageCount,description,authors,genres</c>.
    /// Only <c>isbn</c>, <c>title</c> and <c>category</c> are required. Authors
    /// and genres are <c>;</c>-separated; author order becomes credit order.
    /// Fetch a worked example from <c>GET /api/books/import/template</c>.
    /// </para>
    /// </remarks>
    /// <param name="file">The CSV or JSON file.</param>
    /// <param name="duplicates">
    /// What to do with an ISBN already catalogued: <c>Skip</c> (default),
    /// <c>Update</c>, or <c>Fail</c>.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The import ran. Check the body for per-row failures.</response>
    /// <response code="422">
    /// No file, an empty file, or an unsupported extension. A 422 rather than a
    /// 400 because the multipart request itself is well formed — it is the
    /// content that the rules reject, which is the distinction this API draws
    /// everywhere else.
    /// </response>
    /// <response code="413">The file exceeds the size limit.</response>
    [HttpPost(Name = "ImportBooks")]
    [RequestSizeLimit(MaxUploadBytes)]
    [Consumes("multipart/form-data")]
    [Produces("application/json")]
    [ProducesResponseType<BookImportResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookImportResult>> Import(
        IFormFile file,
        [FromQuery] DuplicateHandling duplicates = DuplicateHandling.Skip,
        CancellationToken cancellationToken = default)
    {
        ImportFormat format = ValidateAndDetectFormat(file);

        // OpenReadStream, not CopyToAsync into a MemoryStream or a temp file.
        // The readers consume it incrementally, so the upload is never held in
        // full - which is the entire point of the IAsyncEnumerable pipeline
        // behind this endpoint.
        await using Stream stream = file.OpenReadStream();

        BookImportResult result = await _importService.ImportAsync(
            stream, format, duplicates, cancellationToken);

        return Ok(result);
    }

    /// <summary>Downloads a CSV template with the expected header and one example row.</summary>
    /// <remarks>
    /// Generated from the same field names the reader binds, so the template
    /// cannot drift from what the importer accepts — a hand-maintained sample
    /// file would.
    /// </remarks>
    /// <response code="200">The template, as a CSV download.</response>
    [HttpGet("template", Name = "GetImportTemplate")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetTemplate()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(_importService.BuildCsvTemplate());

        return File(bytes, "text/csv", "book-import-template.csv");
    }

    /// <summary>
    /// Checks the upload and determines its format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The extension is a usability check, not a security boundary.</b> It is
    /// caller-supplied and trivially faked. What actually protects the system is
    /// that the file is only ever <i>parsed</i> — never written to disk, never
    /// executed, never used to build a path. A renamed executable uploaded here
    /// fails to parse as CSV and is reported as an unreadable row.
    /// </para>
    /// <para>
    /// <c>Path.GetFileName</c> strips any directory component, so a name like
    /// <c>../../etc/passwd</c> cannot traverse anywhere. That matters even though
    /// nothing is written, because the name is echoed in an error message.
    /// </para>
    /// </remarks>
    private static ImportFormat ValidateAndDetectFormat(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            throw new BusinessRuleViolationException(
                "import.file_required", "A non-empty file is required.");
        }

        if (file.Length > MaxUploadBytes)
        {
            throw new BusinessRuleViolationException(
                "import.file_too_large",
                $"The file exceeds the {MaxUploadBytes / (1024 * 1024)} MB limit.");
        }

        string name = Path.GetFileName(file.FileName ?? string.Empty);
        string extension = Path.GetExtension(name).ToLowerInvariant();

        if (AllowedCsvExtensions.Contains(extension))
        {
            return ImportFormat.Csv;
        }

        if (AllowedJsonExtensions.Contains(extension))
        {
            return ImportFormat.Json;
        }

        throw new BusinessRuleViolationException(
            "import.unsupported_file_type",
            $"'{extension}' is not supported. Upload a .csv or .json file.");
    }
}
