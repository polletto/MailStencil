using System.Globalization;

namespace MailStencil;

/// <summary>Identifies an exact template variant in a reader's configured namespace.</summary>
/// <remarks>Names and versions are opaque, case-sensitive identifiers, not paths or URLs.
/// Null culture selects the default variant; null version selects the active version.
/// Readers must not silently ignore a requested version or perform culture fallback.</remarks>
public sealed record TemplateRequest
{
    /// <summary>Creates an exact lookup request.</summary>
    /// <param name="name">Nonblank logical template name.</param>
    /// <param name="culture">Culture name, or null for the default variant. Empty means invariant/default.</param>
    /// <param name="version">Opaque nonblank version, or null for the active version.</param>
    public TemplateRequest(string name, string? culture = null, string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (version is not null) ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Name = name;
        Culture = string.IsNullOrEmpty(culture) ? null : CultureInfo.GetCultureInfo(culture).Name;
        Version = version;
    }

    /// <summary>Gets the logical name.</summary>
    public string Name { get; }
    /// <summary>Gets the normalized culture name, or null for the default variant.</summary>
    public string? Culture { get; }
    /// <summary>Gets the requested version, or null for the active version.</summary>
    public string? Version { get; }
}
