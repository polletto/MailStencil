using MailStencil;
using MailStencil.AzureBlob;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the exact, read-only Azure Blob template provider.</summary>
public static class MailStencilAzureBlobServiceCollectionExtensions
{
    /// <summary>Registers one singleton reader with validated, snapshotted options. The application must
    /// register an Azure.Storage.Blobs.BlobServiceClient before resolution. No infrastructure is created.</summary>
    /// <param name="services">The collection to configure.</param>
    /// <param name="configure">Configures the container, optional prefix and size bound.</param>
    /// <returns>The original collection.</returns>
    /// <exception cref="ArgumentNullException">The collection or callback is null.</exception>
    /// <exception cref="InvalidOperationException">A reader is already registered, including repeated Azure registration.</exception>
    public static IServiceCollection AddAzureBlobTemplateReader(this IServiceCollection services,
        Action<AzureBlobTemplateOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(d => d.ServiceType == typeof(ITemplateReader)))
            throw new InvalidOperationException("Only one template reader may be registered. Remove the existing reader before selecting Azure Blob.");
        services.AddLogging();
        services.AddOptions<AzureBlobTemplateOptions>().Configure(configure)
            .Validate(o => BlobNames.ValidContainer(o.ContainerName), "ContainerName must be a valid 3–63 character lowercase Azure container name.")
            .Validate(o => BlobNames.ValidPrefix(o.Prefix), "Prefix must contain safe identifier segments and be at most 512 normalized characters.")
            .Validate(o => o.MaxTemplateBlobSize is > 0 and <= 16 * 1024 * 1024, "MaxTemplateBlobSize must be between 1 byte and 16 MiB.");
        services.AddSingleton<ITemplateReader, AzureBlobTemplateReader>();
        return services;
    }
}

