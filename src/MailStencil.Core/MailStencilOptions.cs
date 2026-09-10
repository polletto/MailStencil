namespace MailStencil;

/// <summary>Engine-independent runtime defaults. These are configuration, not mutable runtime state.</summary>
public sealed class MailStencilOptions
{
    /// <summary>Gets or sets the default culture name. Empty selects invariant formatting and the default
    /// template variant. Explicit request cultures override this setting. No ambient culture is implied.</summary>
    public string DefaultCulture { get; set; } = string.Empty;

    /// <summary>Gets or sets the absolute lifetime of successfully loaded exact template sources.
    /// Defaults to five minutes. Zero disables caching; negative values are invalid. Missing results,
    /// models and rendered output are never cached. Direct reader calls bypass this cache.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);
}
