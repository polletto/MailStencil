namespace MailStencil;

/// <summary>A coherent snapshot returned by a template reader.</summary>
public sealed class EmailTemplateSource
{
    /// <summary>Creates a snapshot with its exact lookup identity and optional metadata.</summary>
    public EmailTemplateSource(TemplateRequest request, EmailTemplateContent content, TemplateMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        Request = request;
        Content = content;
        Metadata = metadata;
    }

    /// <summary>Gets the exact lookup fulfilled by this snapshot. Resolved active versions belong in metadata.</summary>
    public TemplateRequest Request { get; }
    /// <summary>Gets the unrendered content.</summary>
    public EmailTemplateContent Content { get; }
    /// <summary>Gets optional snapshot metadata.</summary>
    public TemplateMetadata? Metadata { get; }
}
