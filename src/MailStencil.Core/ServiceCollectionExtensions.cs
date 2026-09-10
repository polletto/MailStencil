using System.Globalization;
using MailStencil;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers MailStencil options and default runtime orchestration.</summary>
public static class MailStencilServiceCollectionExtensions
{
    /// <summary>Adds validated options and returns the original collection for provider extension chaining.
    /// Adds a singleton IEmailTemplateService default without replacing a custom service. Reader and
    /// renderer dependencies must be registered before resolution, in any order. Repeated calls compose configuration.</summary>
    public static IServiceCollection AddMailStencil(this IServiceCollection services,
        Action<MailStencilOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.AddOptions<MailStencilOptions>().Validate(options => IsCulture(options.DefaultCulture),
            "DefaultCulture must be a valid culture name (empty selects invariant culture).")
            .Validate(options => options.CacheDuration >= TimeSpan.Zero, "CacheDuration must not be negative.");
        if (configure is not null) services.Configure(configure);
        services.TryAddSingleton<IEmailTemplateService, EmailTemplateService>();
        return services;
    }

    private static bool IsCulture(string? name)
    {
        if (name is null) return false;
        try { _ = CultureInfo.GetCultureInfo(name); return true; }
        catch (CultureNotFoundException) { return false; }
    }
}

