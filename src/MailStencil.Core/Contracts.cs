using System.Globalization;

namespace MailStencil;

/// <summary>Reads exact template snapshots without requiring write access.</summary>
/// <remarks>Implementations must support concurrent calls. Return null only for a missing template
/// variant/version; propagate cancellation, authorization, transport and malformed-content failures.
/// Unsupported explicit version selection must throw <see cref="NotSupportedException"/>.</remarks>
public interface ITemplateReader
{
    /// <summary>Reads an exact snapshot, or returns null when it does not exist.</summary>
    Task<EmailTemplateSource?> GetAsync(TemplateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Renders content with a strongly typed model, without accessing template storage.</summary>
/// <remarks>Implementations must support concurrent calls and must not mutate the model or culture.
/// This contract can also render unsaved content for preview. The declared model type, including
/// declared nested and collection element types, defines the member contract; runtime subtype
/// members must not be exposed automatically. A null root model is invalid.</remarks>
public interface ITemplateRenderer
{
    /// <summary>Renders all content parts using an explicit formatting culture.</summary>
    /// <typeparam name="TModel">The declared model type used for member discovery.</typeparam>
    /// <exception cref="ArgumentNullException">An API argument is null.</exception>
    /// <exception cref="TemplateValidationException">Template content or its declared model contract fails static validation.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    /// <exception cref="TemplateRenderingException">Model projection or template execution fails.</exception>
    Task<RenderedEmailTemplate> RenderAsync<TModel>(EmailTemplateContent content, TModel model,
        CultureInfo culture, CancellationToken cancellationToken = default) where TModel : notnull;
}

/// <summary>The runtime entry point for loading and rendering a template.</summary>
/// <remarks>The default singleton coordinates exact reader lookups, culture-parent fallback,
/// positive source caching and rendering. Reader and renderer dependencies must be safe for
/// concurrent use and suitable for a singleton lifetime. No rendered output or model is cached.</remarks>
public interface IEmailTemplateService
{
    /// <summary>Renders a named active template using configured culture defaults.</summary>
    /// <exception cref="TemplateNotFoundException">All exact fallback variants are absent.</exception>
    Task<RenderedEmailTemplate> RenderAsync<TModel>(string name, TModel model,
        CancellationToken cancellationToken = default) where TModel : notnull;

    /// <summary>Renders a requested variant/version. Null culture uses the default variant.
    /// Formatting always uses the originally requested culture, or invariant culture for null,
    /// even when a parent/default variant is found. Name and version are preserved during fallback.</summary>
    /// <exception cref="TemplateNotFoundException">All exact fallback variants are absent.</exception>
    Task<RenderedEmailTemplate> RenderAsync<TModel>(TemplateRequest request, TModel model,
        CancellationToken cancellationToken = default) where TModel : notnull;
}
