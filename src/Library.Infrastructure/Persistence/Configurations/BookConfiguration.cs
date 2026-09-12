using Library.Domain.Entities;
using Library.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Library.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Book"/> to the <c>Books</c> table.
/// </summary>
/// <remarks>
/// <para>
/// All mapping lives in classes like this one rather than as attributes on the
/// entity. That is what keeps <c>Library.Domain</c> free of any EF Core
/// reference: the entity describes the business, this class describes the table,
/// and swapping the ORM would touch only this project.
/// </para>
/// <para>
/// It also means column widths, indexes, and delete behaviour sit together in one
/// readable place instead of being scattered across properties.
/// </para>
/// </remarks>
public sealed class BookConfiguration : IEntityTypeConfiguration<Book>
{
    public void Configure(EntityTypeBuilder<Book> builder)
    {
        builder.ToTable("Books");

        builder.HasKey(b => b.Id);

        // ---------------------------------------------------------------
        // ISBN maps from the private _isbn STRING FIELD, not from the Isbn
        // value-object property.
        //
        // A value converter would also work for equality, but it makes EF apply
        // the conversion to both sides of every comparison - so a LIKE pattern
        // such as "%design%" gets fed to the Isbn converter and throws at
        // parameter binding. Mapping the plain field leaves an ordinary text
        // column that LIKE, ranges and indexes all treat normally, while the
        // domain still exposes a validated Isbn. See Book.Isbn for the full note.
        // ---------------------------------------------------------------
        builder.Property<string>(Book.IsbnPropertyName)
            .HasColumnName("Isbn")
            .HasMaxLength(Isbn.Length)
            .IsRequired();

        // The Isbn property is a wrapper over that field, not a column of its own.
        builder.Ignore(b => b.Isbn);

        // The catalogue's natural key. Unique because one ISBN is one title -
        // this index is what stops the same book being catalogued twice, and it
        // is what makes lookup-by-ISBN an index seek rather than a scan.
        builder.HasIndex(Book.IsbnPropertyName)
            .IsUnique()
            .HasDatabaseName("IX_Books_Isbn");

        builder.Property(b => b.Title)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(b => b.Subtitle)
            .HasMaxLength(500);

        builder.Property(b => b.Language)
            .HasMaxLength(10);

        builder.Property(b => b.Description)
            .HasMaxLength(4000);

        // Supports the "sort by title" default on the listing endpoint. Without
        // it, every page of a large catalogue costs a full sort.
        builder.HasIndex(b => b.Title)
            .HasDatabaseName("IX_Books_Title");

        // ---------------------------------------------------------------
        // Relationships
        // ---------------------------------------------------------------

        // Restrict, not Cascade: deleting a category must not silently delete
        // every book filed under it. The delete fails instead, and the librarian
        // is told to re-file the books first. Cascade here would be a data-loss
        // bug waiting for a careless click.
        builder.HasOne(b => b.Category)
            .WithMany(c => c.Books)
            .HasForeignKey(b => b.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(b => b.Publisher)
            .WithMany(p => p.Books)
            .HasForeignKey(b => b.PublisherId)
            .OnDelete(DeleteBehavior.SetNull);

        // Backing-field access: the collections are exposed as IReadOnlyCollection
        // so callers must go through Book's methods. EF Core writes to the private
        // list directly, which is how encapsulation survives persistence.
        builder.Metadata.FindNavigation(nameof(Book.BookAuthors))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.Metadata.FindNavigation(nameof(Book.BookGenres))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.Metadata.FindNavigation(nameof(Book.Copies))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // Computed from the loaded Copies collection; there is no such column.
        builder.Ignore(b => b.TotalCopies);
        builder.Ignore(b => b.AvailableCopies);

        builder.HasIndex(b => b.CategoryId).HasDatabaseName("IX_Books_CategoryId");
        builder.HasIndex(b => b.PublisherId).HasDatabaseName("IX_Books_PublisherId");
    }
}
