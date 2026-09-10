namespace MailStencil;

/// <summary>No template exists after all valid exact culture fallback lookups return null.</summary>
/// <remarks>Provider capability, storage, validation and rendering errors remain distinct and are not wrapped.</remarks>
public sealed class TemplateNotFoundException : Exception
{
    /// <summary>Creates a missing-template failure preserving the original request.</summary>
    /// <param name="request">The original requested name, culture and version.</param>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public TemplateNotFoundException(TemplateRequest request) : base("No template was found for the request or its culture fallback variants.")
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    /// <summary>Gets the original request, before any culture fallback.</summary>
    public TemplateRequest Request { get; }
}
