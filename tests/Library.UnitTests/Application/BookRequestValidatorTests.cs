using FluentValidation.Results;
using Library.Application.Books.Requests;
using Library.Application.Books.Validators;
using Library.UnitTests.Common;

namespace Library.UnitTests.Application;

/// <summary>
/// Verifies the request validators, including the date rules that were
/// previously untestable.
/// </summary>
/// <remarks>
/// <para>
/// These tests are the reason <c>IClock</c> was injected into the validators.
/// While they read <c>DateTime.UtcNow</c> directly, "publication date cannot be
/// in the future" could only be exercised by changing the machine's clock — so
/// in practice it was never exercised at all, and a regression in it would have
/// been invisible.
/// </para>
/// <para>
/// With the clock injected, "the day after tomorrow" is just a value.
/// </para>
/// </remarks>
public sealed class BookRequestValidatorTests
{
    private static readonly DateOnly Today =
        DateOnly.FromDateTime(FakeClock.DefaultNow.UtcDateTime);

    private static CreateBookRequest ValidRequest() => new()
    {
        Isbn = "9780132350884",
        Title = "Clean Code",
        CategoryId = 1,
    };

    private static CreateBookRequestValidator CreateValidator() => new(new FakeClock());

    [Fact]
    public void A_valid_request_passes()
    {
        ValidationResult result = CreateValidator().Validate(ValidRequest());

        result.IsValid.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // The date rule - the one the injected clock exists for.
    // -----------------------------------------------------------------------

    [Fact]
    public void A_future_publication_date_is_rejected()
    {
        CreateBookRequest request = ValidRequest() with
        {
            PublishedOn = Today.AddDays(30),
        };

        ValidationResult result = CreateValidator().Validate(request);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e =>
            e.PropertyName == nameof(CreateBookRequest.PublishedOn));
    }

    [Fact]
    public void Todays_date_is_accepted()
    {
        ValidationResult result = CreateValidator()
            .Validate(ValidRequest() with { PublishedOn = Today });

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Tomorrow_is_accepted_as_timezone_tolerance()
    {
        // The rule allows one day of slack deliberately: the server runs in UTC,
        // and a book published "today" in UTC+13 is briefly tomorrow from here.
        // Rejecting it would fail a legitimate request for a few hours a day.
        ValidationResult result = CreateValidator()
            .Validate(ValidRequest() with { PublishedOn = Today.AddDays(1) });

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_long_past_publication_date_is_accepted()
    {
        // Pride and Prejudice, 1813. An upper bound is correct; a lower one
        // would be arbitrary and would reject genuine antiquarian holdings.
        ValidationResult result = CreateValidator()
            .Validate(ValidRequest() with { PublishedOn = new DateOnly(1813, 1, 28) });

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void The_rule_follows_the_clock_rather_than_the_machine_date()
    {
        // The same date is rejected against one clock and accepted against a
        // later one. Impossible to assert before IClock was injected.
        var date = new DateOnly(2030, 6, 1);

        var early = new CreateBookRequestValidator(
            new FakeClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var late = new CreateBookRequestValidator(
            new FakeClock(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        early.Validate(ValidRequest() with { PublishedOn = date }).IsValid.ShouldBeFalse();
        late.Validate(ValidRequest() with { PublishedOn = date }).IsValid.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // ISBN - delegated to the value object, so the check digit is real
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("9780132350884")]          // plain
    [InlineData("978-0-13-235088-4")]      // hyphenated
    public void A_valid_isbn_is_accepted(string isbn)
    {
        CreateValidator().Validate(ValidRequest() with { Isbn = isbn })
            .IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]                       // missing
    [InlineData("123")]                    // too short
    [InlineData("9780345539435")]          // 13 digits, WRONG check digit
    [InlineData("not-an-isbn-at-all")]
    public void An_invalid_isbn_is_rejected(string isbn)
    {
        ValidationResult result =
            CreateValidator().Validate(ValidRequest() with { Isbn = isbn });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(CreateBookRequest.Isbn));
    }

    // -----------------------------------------------------------------------
    // Everything else
    // -----------------------------------------------------------------------

    [Fact]
    public void A_missing_title_is_rejected()
    {
        CreateValidator().Validate(ValidRequest() with { Title = "" })
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_missing_category_is_rejected()
    {
        CreateValidator().Validate(ValidRequest() with { CategoryId = 0 })
            .IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_page_count_is_rejected(int pageCount)
    {
        CreateValidator().Validate(ValidRequest() with { PageCount = pageCount })
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void The_same_author_twice_is_rejected()
    {
        CreateValidator().Validate(ValidRequest() with { AuthorIds = [3, 7, 3] })
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Every_failure_is_reported_together()
    {
        // One round trip should tell the caller everything that is wrong, not
        // the first thing. This is the behaviour the ValidationFilter relies on.
        ValidationResult result = CreateValidator().Validate(new CreateBookRequest
        {
            Isbn = "",
            Title = "",
            CategoryId = 0,
            PageCount = -5,
            PublishedOn = Today.AddYears(5),
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.Select(e => e.PropertyName).Distinct().Count().ShouldBeGreaterThanOrEqualTo(5);
    }

    // -----------------------------------------------------------------------
    // Barcode - the regex is a verbatim literal, so the escape is single
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("LIB-001000")]
    [InlineData("LIB_001000")]
    [InlineData("ABC123")]
    public void A_valid_barcode_is_accepted(string barcode)
    {
        new AddBookCopyRequestValidator(new FakeClock())
            .Validate(new AddBookCopyRequest { Barcode = barcode })
            .IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("BAD CODE!!")]     // space and punctuation
    [InlineData("LIB/001")]        // slash
    [InlineData("")]               // missing
    public void An_invalid_barcode_is_rejected(string barcode)
    {
        new AddBookCopyRequestValidator(new FakeClock())
            .Validate(new AddBookCopyRequest { Barcode = barcode })
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_future_acquisition_date_is_rejected()
    {
        new AddBookCopyRequestValidator(new FakeClock())
            .Validate(new AddBookCopyRequest
            {
                Barcode = "LIB-001000",
                AcquiredOn = Today.AddDays(30),
            })
            .IsValid.ShouldBeFalse();
    }
}
