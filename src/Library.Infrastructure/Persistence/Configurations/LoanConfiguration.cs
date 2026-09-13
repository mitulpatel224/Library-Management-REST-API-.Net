using Library.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Library.Infrastructure.Persistence.Configurations;

public sealed class LoanConfiguration : IEntityTypeConfiguration<Loan>
{
    /// <summary>
    /// Named here because <c>LibraryDbContext</c> has to reach the same index to
    /// attach the provider-specific filter, and a literal repeated in two files
    /// is a rename waiting to go wrong.
    /// </summary>
    public const string ActiveLoanIndexName = "UX_Loans_BookCopyId_Active";

    public void Configure(EntityTypeBuilder<Loan> builder)
    {
        builder.ToTable("Loans");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.IssuedAt).IsRequired();
        builder.Property(l => l.DueAt).IsRequired();
        builder.Property(l => l.ReturnedAt);

        // -------------------------------------------------------------------
        // THE constraint this whole phase is built around.
        //
        // "A copy that is out cannot be issued again" is checked twice in code -
        // BookCopy.MarkOnLoan() and LoanService - and both checks READ before
        // they WRITE. Two concurrent requests can pass both and issue the same
        // copy twice. Only the database can settle that, because only the
        // database serialises the writes.
        //
        // The filter is what makes the index expressible at all. Loan(BookCopyId)
        // cannot be unique outright - a copy is lent hundreds of times over its
        // life. It is unique only among rows where ReturnedAt IS NULL, which is
        // exactly "currently out".
        //
        // Both targets implement this natively - SQL Server calls them "filtered
        // indexes", SQLite "partial indexes" - but HasFilter() takes RAW SQL that
        // EF Core passes through untranslated, identifier quoting included. So
        // the filter text itself is provider-specific even though the feature is
        // not, and it is applied in LibraryDbContext.OnModelCreating where the
        // configured provider is known. Hard-coding "[ReturnedAt] IS NULL" here
        // would emit SQL Server bracket syntax into a SQLite migration.
        //
        // The violation surfaces as DbUpdateException and is translated to a 409
        // by UnitOfWork. The pre-check is for the error message; THIS is the rule.
        // -------------------------------------------------------------------
        builder.HasIndex(l => l.BookCopyId)
            .IsUnique()
            .HasDatabaseName(ActiveLoanIndexName);

        // "What has this member got out?" and "who has this copy?" are the two
        // questions the desk asks all day.
        builder.HasIndex(l => new { l.MemberId, l.ReturnedAt })
            .HasDatabaseName("IX_Loans_MemberId_ReturnedAt");

        // Drives the overdue report: open loans, ordered by how late they are.
        builder.HasIndex(l => new { l.ReturnedAt, l.DueAt })
            .HasDatabaseName("IX_Loans_ReturnedAt_DueAt");

        // RESTRICT on both ends. A copy with loan history cannot be deleted out
        // from under it, and neither can a member - the history is the record of
        // who had what, and it outlives both. Cancelling a membership is retained
        // rather than deleted for exactly this reason.
        builder.HasOne(l => l.BookCopy)
            .WithMany()
            .HasForeignKey(l => l.BookCopyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.Member)
            .WithMany()
            .HasForeignKey(l => l.MemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class FineConfiguration : IEntityTypeConfiguration<Fine>
{
    public void Configure(EntityTypeBuilder<Fine> builder)
    {
        builder.ToTable("Fines");
        builder.HasKey(f => f.Id);

        // decimal(10,2), not the provider default. SQL Server's default mapping
        // for decimal is (18,2) and SQLite stores it as TEXT; naming the precision
        // means the two agree, and 10 digits is more rupees than MaxAmount allows.
        // Money must never reach a floating-point type: 0.1 + 0.2 != 0.3 in binary
        // floating point, and a fine is a legal claim on someone's money.
        builder.Property(f => f.Amount).HasPrecision(10, 2).IsRequired();
        builder.Property(f => f.RatePerDay).HasPrecision(10, 2).IsRequired();

        builder.Property(f => f.DaysOverdue).IsRequired();
        builder.Property(f => f.WaivedReason).HasMaxLength(500);

        // One fine per loan. A second assessment for the same return is a bug -
        // most likely the same domain event dispatched twice - and this turns it
        // into a 409 rather than a member charged double.
        builder.HasIndex(f => f.LoanId)
            .IsUnique()
            .HasDatabaseName("UX_Fines_LoanId");

        // "What does this member owe?" - the query that gates borrowing.
        builder.HasIndex(f => new { f.MemberId, f.PaidAt, f.WaivedAt })
            .HasDatabaseName("IX_Fines_MemberId_Settlement");

        builder.HasOne(f => f.Loan)
            .WithOne(l => l.Fine)
            .HasForeignKey<Fine>(f => f.LoanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.Member)
            .WithMany()
            .HasForeignKey(f => f.MemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
