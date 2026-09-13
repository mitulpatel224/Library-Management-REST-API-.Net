using System.Text;

namespace Library.Domain.Exceptions;

/// <summary>
/// The requested resource does not exist. Maps to <b>404 Not Found</b>.
/// </summary>
/// <remarks>
/// Note what the message deliberately does <i>not</i> include: any data from the
/// entity itself. "Book with id 42 was not found" is safe; echoing back a title
/// or an email address would not be, because a 404 is reachable by an
/// unauthenticated caller.
/// </remarks>
public sealed class NotFoundException : DomainException
{
    public NotFoundException(string resource, object key)
        : base($"{ToSnakeCase(resource)}.not_found",
               $"{resource} with identifier '{key}' was not found.")
    {
        Resource = resource;
        Key = key;
    }

    public string Resource { get; }

    public object Key { get; }

    /// <summary><c>MembershipType</c> becomes <c>membership_type</c>.</summary>
    /// <remarks>
    /// The resource name arrives PascalCase because it is also the readable half
    /// of the message. A plain ToLowerInvariant() gave <c>membershiptype</c>,
    /// which sits beside <c>membership_type.duplicate_name</c> in the same API -
    /// two conventions for a client to learn, where the contract promises one.
    /// Single-word resources are unaffected, so no existing code changes.
    /// </remarks>
    private static string ToSnakeCase(string resource)
    {
        var builder = new StringBuilder(resource.Length + 4);

        for (int i = 0; i < resource.Length; i++)
        {
            char character = resource[i];

            if (char.IsUpper(character) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
