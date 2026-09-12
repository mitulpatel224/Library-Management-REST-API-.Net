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
        : base($"{resource.ToLowerInvariant()}.not_found",
               $"{resource} with identifier '{key}' was not found.")
    {
        Resource = resource;
        Key = key;
    }

    public string Resource { get; }

    public object Key { get; }
}
