namespace MailStencil;

/// <summary>Optional metadata describing a complete template snapshot.</summary>
/// <remarks>Tokens are opaque. An ETag must describe all content parts, not just one storage object.
/// Native object versions need not equal logical template versions.</remarks>
public sealed class TemplateMetadata
{
    /// <summary>Gets the provider's aggregate change token, when available.</summary>
    public string? ETag { get; init; }
    /// <summary>Gets the resolved logical template version, when available.</summary>
    public string? Version { get; init; }
    /// <summary>Gets the snapshot's last modification time, when available.</summary>
    public DateTimeOffset? LastModified { get; init; }
}
