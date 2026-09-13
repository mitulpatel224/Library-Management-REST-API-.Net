using Library.Domain.Entities;
using Library.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Library.Infrastructure.Persistence.Configurations;

public sealed class MembershipTypeConfiguration : IEntityTypeConfiguration<MembershipType>
{
    public void Configure(EntityTypeBuilder<MembershipType> builder)
    {
        builder.ToTable("MembershipTypes");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).HasMaxLength(50).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(500);
        builder.Property(t => t.MaxConcurrentLoans).IsRequired();
        builder.Property(t => t.LoanPeriodDays).IsRequired();

        builder.HasIndex(t => t.Name)
            .IsUnique()
            .HasDatabaseName("IX_MembershipTypes_Name");

        builder.Metadata.FindNavigation(nameof(MembershipType.Members))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        builder.ToTable("Members");
        builder.HasKey(m => m.Id);

        // An ordinary string column. The private setter is no obstacle - EF Core
        // writes through it, which is what keeps assignment confined to
        // AssignMembershipNumber().
        builder.Property(m => m.MembershipNumber)
            .HasMaxLength(20)
            .IsRequired();

        builder.HasIndex(m => m.MembershipNumber)
            .IsUnique()
            .HasDatabaseName("IX_Members_MembershipNumber");

        builder.Property(m => m.FullName)
            .HasMaxLength(Member.MaxNameLength)
            .IsRequired();

        // ---------------------------------------------------------------
        // Ordinary string columns - NO value converters, matching Book.Isbn.
        //
        // A converter is applied to both sides of a comparison, which makes the
        // column unusable for LIKE (member search) and ORDER BY (?sortBy=email).
        // The Email and PhoneNumber value objects still validate and normalise
        // at construction; the entity stores what they produced.
        //
        // The widths come from the value objects, so the types and the schema
        // cannot disagree about how long an address or a number may be.
        // ---------------------------------------------------------------
        builder.Property(m => m.Email)
            .HasMaxLength(Email.MaxLength)
            .IsRequired();

        // Unique because an email address identifies a person. Two member
        // records sharing one is a duplicate registration, and the index is what
        // catches it under concurrency - the service check only produces the
        // friendly message.
        builder.HasIndex(m => m.Email)
            .IsUnique()
            .HasDatabaseName("IX_Members_Email");

        // Nullable: a member may register without a phone number.
        builder.Property(m => m.Phone)
            .HasMaxLength(PhoneNumber.MaxDigits + 1);   // +1 for the leading '+'

        builder.Property(m => m.Address).HasMaxLength(500);
        builder.Property(m => m.StatusReason).HasMaxLength(500);

        builder.Property(m => m.Status).HasConversion<int>().IsRequired();
        builder.Property(m => m.JoinedOn).IsRequired();

        // Restrict, not Cascade: deleting a membership type must not delete
        // every member holding it. The delete fails and the librarian re-assigns
        // those members first.
        builder.HasOne(m => m.MembershipType)
            .WithMany(t => t.Members)
            .HasForeignKey(m => m.MembershipTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        // Supports the default listing sort and name search.
        builder.HasIndex(m => m.FullName).HasDatabaseName("IX_Members_FullName");

        // Composite for the common "active members of type X" filter. Status
        // first because it is the more selective predicate in practice - most
        // members are Active, but a status filter is almost always supplied.
        builder.HasIndex(m => new { m.Status, m.MembershipTypeId })
            .HasDatabaseName("IX_Members_Status_MembershipTypeId");

        builder.Ignore(m => m.CanBorrow);
    }
}
