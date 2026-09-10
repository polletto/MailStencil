using MailStencil;
using MailStencil.FileSystem;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the read-only filesystem template provider.</summary>
public static class MailStencilFileSystemServiceCollectionExtensions
{
    /// <summary>Registers one singleton reader with validated, snapshotted configuration.
    /// Rejects registration if an ITemplateReader is already registered, including another call
    /// to this method. Does not register rendering, validation, fallback or a template service.</summary>
    /// <exception cref="ArgumentNullException">Services or configuration callback is null.</exception>
    /// <exception cref="InvalidOperationException">A template reader is already registered.</exception>
    public static IServiceCollection AddFileSystemTemplateReader(this IServiceCollection services,
        Action<FileSystemTemplateOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(d => d.ServiceType == typeof(ITemplateReader)))
            throw new InvalidOperationException("Only one template reader may be registered. Remove the existing reader before selecting FileSystem.");
        services.AddLogging();
        services.AddOptions<FileSystemTemplateOptions>()
            .Configure(configure)
            .Validate(o => FileSystemTemplateReader.IsValidBasePath(o.BasePath), "BasePath must be a nonblank valid filesystem path.")
            .Validate(o => o.MaxTemplateFileSize is > 0 and <= 16 * 1024 * 1024,
                "MaxTemplateFileSize must be between 1 byte and 16 MiB.");
        services.AddSingleton<ITemplateReader, FileSystemTemplateReader>();
        return services;
    }
}

