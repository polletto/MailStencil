using MailStencil;
using MailStencil.Scriban;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Scriban rendering and validation without storage or runtime service orchestration.</summary>
public static class MailStencilScribanServiceCollectionExtensions
{
    /// <summary>Adds thread-safe singleton renderer and validator defaults, preserving existing registrations.</summary>
    public static IServiceCollection AddScribanRenderer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ITemplateRenderer, ScribanTemplateEngine>();
        services.TryAddSingleton<ITemplateValidator, ScribanTemplateEngine>();
        return services;
    }
}
