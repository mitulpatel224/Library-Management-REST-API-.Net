using FluentValidation;
using Library.Application.Books.Requests;
using Library.Application.Common.Abstractions;
using Library.Domain.ValueObjects;

namespace Library.Application.Books.Validators;

/// <summary>
/// Validation for <see cref="CreateBookRequest"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why FluentValidation rather than data annotations.</b> Annotations put
/// validation on the DTO as attributes, which works for "required" and "max
/// length" and runs out of road immediately afterwards: a rule that needs two
/// properties, or a database lookup, has nowhere to live. Keeping rules in a
/// separate class also means the DTO stays a plain data contract.
/// </para>
/// <para>
/// <b>Why the ISBN rule calls the domain.</b> <c>Isbn.TryCreate</c> already
/// encodes the format and check-digit rules. Re-implementing them here would be
/// a second definition of the same rule, free to drift. The validator produces
/// the friendly per-field message; the value object remains the authority.
/// </para>
/// <para>
/// <b>What is NOT validated here.</b> Uniqueness of the ISBN, and whether the
/// referenced category or publisher exists. Those need the database, and a
/// validator that queries would both slow every request and create a
/// check-then-act race. They are enforced in the service and, ultimately, by
/// database constraints.
/// </para>
/// </remarks>
public sealed class CreateBookRequestValidator : AbstractValidator<CreateBookRequest>
{
    /// <param name="clock">
    /// Injected rather than read from <c>DateTime.UtcNow</c>. The "not in the
    /// future" rule below is a comparison against now, so with the clock baked
    /// in there is no way to test it except by changing the machine's date.
    /// The <c>ValidationFilter</c> resolves validators from DI, so constructor
    /// injection works here exactly as it does in a service.
    /// </param>
    public CreateBookRequestValidator(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(r => r.Isbn)
            .NotEmpty().WithMessage("ISBN is required.")
            .Must(BeAValidIsbn)
            .WithMessage("ISBN must be a valid 13-digit ISBN with a correct check digit.");

        RuleFor(r => r.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(500).WithMessage("Title must be 500 characters or fewer.");

        RuleFor(r => r.Subtitle)
            .MaximumLength(500).WithMessage("Subtitle must be 500 characters or fewer.");

        RuleFor(r => r.CategoryId)
            .GreaterThan(0).WithMessage("A category is required.");

        RuleFor(r => r.PublisherId)
            .GreaterThan(0).When(r => r.PublisherId.HasValue)
            .WithMessage("Publisher id must be greater than zero.");

        RuleFor(r => r.PageCount)
            .GreaterThan(0).When(r => r.PageCount.HasValue)
            .WithMessage("Page count must be greater than zero.");

        RuleFor(r => r.Language)
            .MaximumLength(10).WithMessage("Language code must be 10 characters or fewer.");

        RuleFor(r => r.Description)
            .MaximumLength(4000).WithMessage("Description must be 4000 characters or fewer.");

        // A future publication date is almost always a typo. Rejected rather
        // than warned about, because a wrong date silently corrupts every
        // date-range report built on it.
        RuleFor(r => r.PublishedOn)
            .LessThanOrEqualTo(_ => clock.Today.AddDays(1))
            .When(r => r.PublishedOn.HasValue)
            .WithMessage("Publication date cannot be in the future.");

        RuleFor(r => r.AuthorIds)
            .Must(ids => ids.All(id => id > 0))
            .WithMessage("Author ids must be greater than zero.")
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("The same author cannot be listed twice on one book.");

        RuleFor(r => r.GenreIds)
            .Must(ids => ids.All(id => id > 0))
            .WithMessage("Genre ids must be greater than zero.");
    }

    private static bool BeAValidIsbn(string isbn) => Isbn.TryCreate(isbn, out _, out _);
}

public sealed class UpdateBookRequestValidator : AbstractValidator<UpdateBookRequest>
{
    public UpdateBookRequestValidator(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(r => r.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(500).WithMessage("Title must be 500 characters or fewer.");

        RuleFor(r => r.Subtitle)
            .MaximumLength(500).WithMessage("Subtitle must be 500 characters or fewer.");

        RuleFor(r => r.CategoryId)
            .GreaterThan(0).WithMessage("A category is required.");

        RuleFor(r => r.PublisherId)
            .GreaterThan(0).When(r => r.PublisherId.HasValue)
            .WithMessage("Publisher id must be greater than zero.");

        RuleFor(r => r.PageCount)
            .GreaterThan(0).When(r => r.PageCount.HasValue)
            .WithMessage("Page count must be greater than zero.");

        RuleFor(r => r.Language)
            .MaximumLength(10).WithMessage("Language code must be 10 characters or fewer.");

        RuleFor(r => r.Description)
            .MaximumLength(4000).WithMessage("Description must be 4000 characters or fewer.");

        RuleFor(r => r.PublishedOn)
            .LessThanOrEqualTo(_ => clock.Today.AddDays(1))
            .When(r => r.PublishedOn.HasValue)
            .WithMessage("Publication date cannot be in the future.");

        RuleFor(r => r.AuthorIds)
            .Must(ids => ids.All(id => id > 0))
            .WithMessage("Author ids must be greater than zero.")
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("The same author cannot be listed twice on one book.");

        RuleFor(r => r.GenreIds)
            .Must(ids => ids.All(id => id > 0))
            .WithMessage("Genre ids must be greater than zero.");
    }
}

public sealed class AddBookCopyRequestValidator : AbstractValidator<AddBookCopyRequest>
{
    public AddBookCopyRequestValidator(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(r => r.Barcode)
            .NotEmpty().WithMessage("Barcode is required.")
            .MaximumLength(50).WithMessage("Barcode must be 50 characters or fewer.")
            .Matches(@"^[A-Za-z0-9\-_]+$")
            .WithMessage("Barcode may contain only letters, digits, hyphens and underscores.");

        RuleFor(r => r.Condition).IsInEnum().WithMessage("Unknown condition value.");

        RuleFor(r => r.ShelfLocation)
            .MaximumLength(50).WithMessage("Shelf location must be 50 characters or fewer.");

        RuleFor(r => r.AcquiredOn)
            .LessThanOrEqualTo(_ => clock.Today.AddDays(1))
            .When(r => r.AcquiredOn.HasValue)
            .WithMessage("Acquisition date cannot be in the future.");
    }
}

public sealed class UpdateBookCopyRequestValidator : AbstractValidator<UpdateBookCopyRequest>
{
    public UpdateBookCopyRequestValidator()
    {
        // The same barcode rules as creation. Duplicating the three lines is
        // preferable to sharing a base validator here: the two requests are free
        // to diverge, and a shared base would make that a breaking change for
        // both. Three lines of duplication, no coupling.
        RuleFor(r => r.Barcode)
            .NotEmpty().WithMessage("Barcode is required.")
            .MaximumLength(50).WithMessage("Barcode must be 50 characters or fewer.")
            .Matches(@"^[A-Za-z0-9\-_]+$")
            .WithMessage("Barcode may contain only letters, digits, hyphens and underscores.");

        RuleFor(r => r.Condition).IsInEnum().WithMessage("Unknown condition value.");

        RuleFor(r => r.ShelfLocation)
            .MaximumLength(50).WithMessage("Shelf location must be 50 characters or fewer.");
    }
}
