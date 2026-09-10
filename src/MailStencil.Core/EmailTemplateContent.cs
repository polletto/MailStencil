namespace MailStencil;

/// <summary>Immutable engine-neutral template text, independent of storage identity.</summary>
public sealed class EmailTemplateContent
{
    /// <summary>Creates template content. At least one body must be non-null; empty text is permitted.</summary>
    public EmailTemplateContent(string subject, string? htmlBody = null, string? textBody = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (htmlBody is null && textBody is null)
            throw new ArgumentException("At least one body must be present.");
        Subject = subject;
        HtmlBody = htmlBody;
        TextBody = textBody;
    }

    /// <summary>Gets the subject template.</summary>
    public string Subject { get; }
    /// <summary>Gets the HTML template, or null when absent.</summary>
    public string? HtmlBody { get; }
    /// <summary>Gets the plain text template, or null when absent.</summary>
    public string? TextBody { get; }
}
