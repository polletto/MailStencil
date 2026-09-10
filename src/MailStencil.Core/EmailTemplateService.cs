using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailStencil;

internal sealed class EmailTemplateService : IEmailTemplateService, IDisposable
{
    private readonly ITemplateReader reader;
    private readonly ITemplateRenderer renderer;
    private readonly string defaultCulture;
    private readonly TimeSpan cacheDuration;
    private readonly MemoryCache cache;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly ILogger<EmailTemplateService> logger;

    public EmailTemplateService(ITemplateReader reader, ITemplateRenderer renderer, IOptions<MailStencilOptions> options,
        ILogger<EmailTemplateService> logger)
        : this(reader, renderer, options, new MemoryCacheOptions(), logger) { }

    // Only tests supply a cache clock. The service owns this dedicated cache and its disposal.
    internal EmailTemplateService(ITemplateReader reader, ITemplateRenderer renderer, IOptions<MailStencilOptions> options,
        MemoryCacheOptions cacheOptions, ILogger<EmailTemplateService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(options);
        this.reader = reader;
        this.renderer = renderer;
        this.logger = logger ?? NullLogger<EmailTemplateService>.Instance;
        var value = options.Value;
        defaultCulture = CultureInfo.GetCultureInfo(value.DefaultCulture).Name;
        cacheDuration = value.CacheDuration;
        if (cacheDuration < TimeSpan.Zero)
            throw new OptionsValidationException(Options.DefaultName, typeof(MailStencilOptions), ["CacheDuration must not be negative."]);
        cache = new MemoryCache(cacheOptions);
        utcNow = () => cacheOptions.Clock?.UtcNow ?? DateTimeOffset.UtcNow;
    }

    public Task<RenderedEmailTemplate> RenderAsync<TModel>(string name, TModel model,
        CancellationToken cancellationToken = default) where TModel : notnull =>
        RenderAsync(new TemplateRequest(name, defaultCulture), model, cancellationToken);

    public async Task<RenderedEmailTemplate> RenderAsync<TModel>(TemplateRequest request, TModel model,
        CancellationToken cancellationToken = default) where TModel : notnull
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        var formattingCulture = request.Culture is null ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(request.Culture);
        var variant = request;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await LoadExact(variant, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (source is not null)
            {
                try
                {
                    var rendered = await renderer.RenderAsync<TModel>(source.Content, model, formattingCulture, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return rendered;
                }
                catch (TemplateValidationException)
                {
                    logger.LogWarning(new EventId(1007, "ValidationFailed"), "Template validation failed during rendering.");
                    throw;
                }
                catch (TemplateRenderingException)
                {
                    logger.LogWarning(new EventId(1008, "RenderingFailed"), "Template execution failed during rendering.");
                    throw;
                }
            }
            if (variant.Culture is null)
            {
                logger.LogDebug(new EventId(1006, "TemplateNotFound"), "No template variant was found for {Name}, {Culture}, {Version}.", request.Name, request.Culture, request.Version);
                throw new TemplateNotFoundException(request);
            }
            var parent = CultureInfo.GetCultureInfo(variant.Culture).Parent;
            logger.LogDebug(new EventId(1005, "CultureFallback"), "Falling back for {Name} from {Culture} to {ParentCulture} with {Version}.", request.Name, variant.Culture, parent.Name.Length == 0 ? null : parent.Name, request.Version);
            variant = new TemplateRequest(request.Name, parent.Name, request.Version);
        }
    }

    private async Task<EmailTemplateSource?> LoadExact(TemplateRequest request, CancellationToken cancellationToken)
    {
        var key = new CacheKey(request.Name, request.Culture, request.Version);
        if (cacheDuration > TimeSpan.Zero && cache.TryGetValue<EmailTemplateSource>(key, out var cached))
        {
            logger.LogDebug(new EventId(1001, "CacheHit"), "Template source cache hit for {Name}, {Culture}, {Version}.", request.Name, request.Culture, request.Version);
            return cached;
        }
        if (cacheDuration > TimeSpan.Zero)
            logger.LogDebug(new EventId(1002, "CacheMiss"), "Template source cache miss for {Name}, {Culture}, {Version}.", request.Name, request.Culture, request.Version);
        var source = await reader.GetAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogDebug(source is null ? new EventId(1003, "ExactVariantMissing") : new EventId(1004, "SourceLoaded"),
            "Exact template lookup for {Name}, {Culture}, {Version}: {Found}.", request.Name, request.Culture, request.Version, source is not null);
        if (source is not null && cacheDuration > TimeSpan.Zero)
        {
            var now = utcNow();
            // All nonnegative TimeSpans are valid, including those beyond the calendar's range.
            var expiration = cacheDuration >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + cacheDuration;
            cache.Set(key, source, expiration);
        }
        return source;
    }

    public void Dispose() => cache.Dispose();

    private sealed record CacheKey(string Name, string? Culture, string? Version);
}
