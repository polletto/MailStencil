namespace MailStencil.AzureBlob;

/// <summary>Configuration for exact, read-only Azure Blob template lookups. Credentials belong to the application's Azure client.</summary>
public sealed class AzureBlobTemplateOptions
{
    /// <summary>Gets or sets the existing container name. Required; 3–63 lowercase ASCII letters, digits or single hyphens, with alphanumeric ends.</summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional slash-separated prefix. Empty means none. Redundant slashes are removed.
    /// Segments follow the template identifier policy; the normalized prefix is at most 512 characters.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum complete JSON blob size in bytes, including an optional UTF-8 BOM.
    /// Defaults to 4 MiB; valid values are 1 byte through 16 MiB. Renderer component limits remain independent.</summary>
    public int MaxTemplateBlobSize { get; set; } = 4 * 1024 * 1024;
}
