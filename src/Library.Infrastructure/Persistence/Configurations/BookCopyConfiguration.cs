using Library.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Library.Infrastructure.Persistence.Configurations;

public sealed class BookCopyConfiguration : IEntityTypeConfiguration<BookCopy>
{
    public void Configure(EntityTypeBuilder<BookCopy> builder)
    {
        builder.ToTable("BookCopies");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Barcode)
            .HasMaxLength(50)
            .IsRequired();

        // Library-wide unique, not merely unique per title: a barcode scanned at
        // the desk has to identify exactly one physical item with no further
        // context.
        builder.HasIndex(c => c.Barcode)
            .IsUnique()
            .HasDatabaseName("IX_BookCopies_Barcode");

        builder.Property(c => c.ShelfLocation)
            .HasMaxLength(50);

        // Enums persist as int, matching the explicitly-numbered values in the
        // domain. Storing them as strings would be more readable in raw SQL but
        // would make renaming a member a breaking schema change.
        builder.Property(c => c.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(c => c.Condition)
            .HasConversion<int>()
            .IsRequired();

        // Cascade IS correct here, unlike on Category: a copy has no meaning
        // without its title. Deleting the Book should take its copies with it.
        builder.HasOne(c => c.Book)
            .WithMany(b => b.Copies)
            .HasForeignKey(c => c.BookId)
            .OnDelete(DeleteBehavior.Cascade);

        // Composite index supporting the availability filter on the listing
        // endpoint: "copies of book X that are Available". Column order matters -
        // BookId first because it is the equality predicate, Status second.
        builder.HasIndex(c => new { c.BookId, c.Status })
            .HasDatabaseName("IX_BookCopies_BookId_Status");

        builder.Ignore(c => c.IsAvailable);
    }
}
