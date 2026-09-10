namespace MailStencil.AzureBlob;

/// <summary>A blob contains malformed, unsupported or oversized MailStencil content. Azure operational errors are not wrapped.</summary>
public sealed class AzureBlobTemplateException : IOException
{
    /// <summary>Creates a provider content failure retaining the requested identity and optional underlying cause.</summary>
    /// <param name="request">The exact template request.</param>
    /// <param name="message">A description of the content failure.</param>
    /// <param name="innerException">An optional parsing or encoding cause.</param>
    /// <exception cref="ArgumentNullException">The request or message is null.</exception>
    public AzureBlobTemplateException(TemplateRequest request, string message, Exception? innerException = null)
        : base(message ?? throw new ArgumentNullException(nameof(message)), innerException)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    /// <summary>Gets the exact request whose blob content failed.</summary>
    public TemplateRequest Request { get; }
}
