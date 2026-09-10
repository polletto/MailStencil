namespace MailStencil;

/// <summary>Rendered email content ready for an external email sender.</summary>
public sealed class RenderedEmailTemplate
{
    /// <summary>Creates rendered output. At least one body must be non-null; empty output is permitted.</summary>
    public RenderedEmailTemplate(string subject, string? htmlBody = null, string? textBody = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (htmlBody is null && textBody is null)
            throw new ArgumentException("At least one body must be present.");
        Subject = subject;
        HtmlBody = htmlBody;
        TextBody = textBody;
    }

    /// <summary>Gets the rendered subject.</summary>
    public string Subject { get; }
    /// <summary>Gets rendered HTML, or null when absent.</summary>
    public string? HtmlBody { get; }
    /// <summary>Gets rendered plain text, or null when absent.</summary>
    public string? TextBody { get; }
}
