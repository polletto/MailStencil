using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace MailStencil.AzureBlob;

internal sealed class AzureBlobTemplateReader : ITemplateReader
{
    private readonly BlobContainerClient container;
    private readonly string prefix;
    private readonly int maxSize;
    private readonly ILogger<AzureBlobTemplateReader> logger;

    public AzureBlobTemplateReader(BlobServiceClient client, IOptions<AzureBlobTemplateOptions> options, ILogger<AzureBlobTemplateReader> logger)
    {
        var value = options.Value;
        container = client.GetBlobContainerClient(value.ContainerName);
        prefix = BlobNames.NormalizePrefix(value.Prefix);
        maxSize = value.MaxTemplateBlobSize;
        this.logger = logger;
    }

    public async Task<EmailTemplateSource?> GetAsync(TemplateRequest request, CancellationToken cancellationToken = default)
    {
        try { return await ReadExactAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (AzureBlobTemplateException)
        {
            logger.LogWarning(new EventId(3001, "AzureBlobContentFailed"), "Azure Blob template content is malformed, unsupported or oversized.");
            throw;
        }
    }

    private async Task<EmailTemplateSource?> ReadExactAsync(TemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Version is not null) throw new NotSupportedException("Azure Blob reader does not support MailStencil logical versions.");
        var blob = container.GetBlobClient(BlobNames.Map(prefix, request));
        Response<BlobDownloadStreamingResult> response;
        try
        {
            response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "BlobNotFound")
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        using var rawResponse = response.GetRawResponse();
        using var download = response.Value;
        cancellationToken.ThrowIfCancellationRequested();
        var length = download.Details.ContentLength;
        if (length < 0 || length > maxSize)
            throw new AzureBlobTemplateException(request, "Template blob exceeds the configured size limit or has an invalid length.");
        using var buffer = new MemoryStream((int)length);
        var chunk = new byte[8192];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Read at most the limit plus one sentinel byte, even if ContentLength was inaccurate.
            var count = await download.Content.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maxSize - buffer.Length + 1)), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > maxSize) throw new AzureBlobTemplateException(request, "Template blob exceeds the configured size limit.");
            buffer.Write(chunk, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length != length) throw new AzureBlobTemplateException(request, "Template blob length does not match the download metadata.");
        var content = BlobDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), request);
        cancellationToken.ThrowIfCancellationRequested();
        return new EmailTemplateSource(request, content, new TemplateMetadata
        {
            ETag = download.Details.ETag.ToString(),
            LastModified = download.Details.LastModified,
            Version = null
        });
    }
}
