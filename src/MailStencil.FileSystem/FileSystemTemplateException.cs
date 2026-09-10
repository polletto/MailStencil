namespace MailStencil.FileSystem;

/// <summary>An existing template is malformed, unsafe, oversized or could not be read consistently.</summary>
/// <remarks>Missing exact directories return null instead. Authorization and unrelated I/O failures
/// retain their original exception types. This exception is unrelated to engine validation.</remarks>
public sealed class FileSystemTemplateException : IOException
{
    /// <summary>Creates a provider failure for an exact request.</summary>
    /// <param name="request">The affected request.</param>
    /// <param name="message">A description of the storage problem.</param>
    /// <param name="innerException">An optional underlying cause.</param>
    /// <exception cref="ArgumentNullException">The request or message is null.</exception>
    public FileSystemTemplateException(TemplateRequest request, string message, Exception? innerException = null)
        : base(message ?? throw new ArgumentNullException(nameof(message)), innerException)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    /// <summary>Gets the affected exact template request.</summary>
    public TemplateRequest Request { get; }
}
