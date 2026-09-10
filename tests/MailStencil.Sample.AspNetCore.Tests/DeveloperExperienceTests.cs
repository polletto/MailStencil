using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using MailStencil;
using MailStencil.AzureBlob;
using MailStencil.FileSystem;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailStencil.Sample.AspNetCore.Tests;

public sealed class DeveloperExperienceTests
{
    [Theory]
    [InlineData("", "Order MS-123 confirmed", "42.50")]
    [InlineData("?culture=it-IT", "Ordine MS-123 confermato", "42,50")]
    [InlineData("?culture=fr-CA", "Order MS-123 confirmed", "42,50")]
    public async Task RealSampleEndpointRendersWithFallback(string query, string subject, string total)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<RenderedEmailTemplate>("/preview/order-confirmation" + query);
        Assert.NotNull(response);
        Assert.Equal(subject, response.Subject.Trim());
        Assert.Contains("Ada &amp; friends", response.HtmlBody);
        Assert.Contains("Ada & friends", response.TextBody);
        Assert.Contains(total, response.TextBody);
        Assert.Same(factory.Services.GetRequiredService<IEmailTemplateService>(), factory.Services.GetRequiredService<IEmailTemplateService>());
    }

    [Theory]
    [InlineData("MailStencil:CacheDuration", "-00:00:01")]
    [InlineData("MailStencil:DefaultCulture", "invalid_culture_@")]
    [InlineData("MailStencilFileSystem:BasePath", "")]
    [InlineData("MailStencilFileSystem:MaxTemplateFileSize", "0")]
    public async Task InvalidBoundConfigurationFailsStartup(string key, string value)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData(0, 404, "template-not-found")]
    [InlineData(1, 500, "template-validation")]
    [InlineData(2, 500, "template-rendering")]
    [InlineData(3, 500, "filesystem-content")]
    [InlineData(4, 500, "azure-content")]
    public async Task EndpointReturnsSanitizedDistinctProblems(int kind, int status, string code)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailTemplateService>();
            services.AddSingleton<IEmailTemplateService>(new FailingService(Failure(kind)));
        }));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/preview/order-confirmation");
        Assert.Equal(status, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("urn:mailstencil:" + code, body);
        Assert.DoesNotContain("SECRET", body);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoggingSignalsExcludeContentAndModelUnderConcurrency()
    {
        using var logs = new CaptureProvider();
        using var services = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs))
            .AddSingleton<ITemplateReader, Reader>().AddSingleton<ITemplateRenderer, Renderer>().AddMailStencil()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var service = services.GetRequiredService<IEmailTemplateService>();
        var request = new TemplateRequest("welcome", "it-IT");
        await service.RenderAsync(request, "SECRET_MODEL");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.RenderAsync(request, "SECRET_MODEL")));
        Assert.Contains(logs.Entries, e => e.Event.Name == "CacheMiss" && e.Level == LogLevel.Debug);
        Assert.Contains(logs.Entries, e => e.Event.Name == "CacheHit" && e.Level == LogLevel.Debug);
        Assert.Contains(logs.Entries, e => e.Event.Name == "CultureFallback" && e.Level == LogLevel.Debug);
        AssertSafe(logs);
    }

    [Theory]
    [InlineData(1, "ValidationFailed")]
    [InlineData(2, "RenderingFailed")]
    public async Task FailureSignalsPreserveExceptionsWithoutLoggingThem(int kind, string eventName)
    {
        using var logs = new CaptureProvider();
        var error = Failure(kind);
        using var services = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs))
            .AddSingleton<ITemplateReader, Reader>().AddSingleton<ITemplateRenderer>(new Renderer(error)).AddMailStencil().BuildServiceProvider();
        Assert.Same(error, await Record.ExceptionAsync(() => services.GetRequiredService<IEmailTemplateService>().RenderAsync("welcome", "SECRET_MODEL")));
        Assert.Contains(logs.Entries, e => e.Event.Name == eventName && e.Level == LogLevel.Warning);
        AssertSafe(logs);
    }

    [Fact]
    public async Task LibraryWorksWithoutLoggingProviders()
    {
        using var services = new ServiceCollection().AddSingleton<ITemplateReader, Reader>().AddSingleton<ITemplateRenderer, Renderer>()
            .AddMailStencil().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.Empty(services.GetServices<ILoggerProvider>());
        Assert.Equal("SECRET_RENDERED", (await services.GetRequiredService<IEmailTemplateService>().RenderAsync("welcome", "SECRET_MODEL")).Subject);
    }

    [Fact]
    public async Task FileSystemMalformedContentLogsNoPathsOrContents()
    {
        // A nonexisting tree cannot produce a format error; create only our isolated incomplete variant.
        var root = Path.Combine(PhysicalTempPath(), "MailStencil.Logging-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "SECRET_NAME", "default");
        Directory.CreateDirectory(path);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(path, "subject.txt"), "SECRET_SUBJECT");
            using var logs = new CaptureProvider();
            using var services = new ServiceCollection().AddLogging(b => b.AddProvider(logs))
                .AddFileSystemTemplateReader(o => o.BasePath = root).BuildServiceProvider();
            await Assert.ThrowsAsync<FileSystemTemplateException>(() => services.GetRequiredService<ITemplateReader>().GetAsync(new("SECRET_NAME")));
            Assert.Contains(logs.Entries, e => e.Event.Name == "FileSystemContentFailed" && e.Level == LogLevel.Warning);
            AssertSafe(logs);
            Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(root, StringComparison.Ordinal));
        }
        finally
        {
            // Delete only the concrete test-owned files and directories; no recursive path deletion.
            File.Delete(Path.Combine(path, "subject.txt"));
            Directory.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
            Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData("MSA")]
    [InlineData("AMS")]
    [InlineData("SAM")]
    public async Task AzureAspNetHostValidatesWithoutNetworkAndRenders(string order)
    {
        using var transport = new BlobTransport();
        using var http = new HttpClient(transport);
        var client = new BlobServiceClient(new Uri("https://unit.invalid"), new BlobClientOptions
        { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } });
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(client);
        foreach (var part in order)
            switch (part)
            {
                case 'M': builder.Services.AddMailStencil(); break;
                case 'S': builder.Services.AddScribanRenderer(); break;
                case 'A': builder.Services.AddAzureBlobTemplateReader(o => o.ContainerName = "templates"); break;
            }
        builder.Services.AddOptions<AzureBlobTemplateOptions>().ValidateOnStart();
        await using var app = builder.Build();
        await app.StartAsync();
        Assert.Equal(0, transport.Calls);
        var rendered = await app.Services.GetRequiredService<IEmailTemplateService>().RenderAsync("welcome", new OrderConfirmationModel
        { CustomerName = "Ada & friends", OrderNumber = "1", Total = 2m });
        Assert.Equal("Hi Ada & friends", rendered.Subject);
        Assert.Equal("Ada &amp; friends", rendered.HtmlBody);
        Assert.Equal(1, transport.Calls);
        await app.StopAsync();
    }

    [Fact]
    public async Task AzureFormatLoggingDoesNotExposeJsonOrUri()
    {
        using var logs = new CaptureProvider();
        using var transport = new BlobTransport { Document = "SECRET_JSON" };
        using var http = new HttpClient(transport);
        var client = new BlobServiceClient(new Uri("https://SECRET_HOST.invalid"), new BlobClientOptions
        { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } });
        using var services = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs))
            .AddSingleton(client).AddAzureBlobTemplateReader(o => { o.ContainerName = "templates"; o.Prefix = "SECRET_PREFIX"; }).BuildServiceProvider();
        await Assert.ThrowsAsync<AzureBlobTemplateException>(() => services.GetRequiredService<ITemplateReader>().GetAsync(new("SECRET_NAME")));
        Assert.Contains(logs.Entries, e => e.Event.Name == "AzureBlobContentFailed" && e.Level == LogLevel.Warning);
        AssertSafe(logs);
    }

    [Fact]
    public async Task AzureInvalidOptionsFailHostStartAndReadersAreExclusive()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new BlobServiceClient(new Uri("https://unit.invalid")));
        builder.Services.AddAzureBlobTemplateReader(o => o.ContainerName = "INVALID");
        builder.Services.AddOptions<AzureBlobTemplateOptions>().ValidateOnStart();
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddFileSystemTemplateReader(o => o.BasePath = "Templates"));
        await using var app = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
        var reverse = new ServiceCollection().AddFileSystemTemplateReader(o => o.BasePath = "Templates");
        Assert.Throws<InvalidOperationException>(() => reverse.AddAzureBlobTemplateReader(o => o.ContainerName = "templates"));
    }

    private static Exception Failure(int kind) => kind switch
    {
        0 => new TemplateNotFoundException(new("SECRET_NAME")),
        1 => new TemplateValidationException(new TemplateValidationResult([new(TemplateComponent.Subject, TemplateDiagnosticSeverity.Error, "test", "SECRET_DIAGNOSTIC")])),
        2 => new TemplateRenderingException(new(TemplateComponent.General, TemplateDiagnosticSeverity.Error, "test", "SECRET_DIAGNOSTIC"), new Exception("SECRET_INNER")),
        3 => new FileSystemTemplateException(new("SECRET_NAME"), "SECRET_PATH"),
        _ => new AzureBlobTemplateException(new("SECRET_NAME"), "SECRET_JSON")
    };

    private static string PhysicalTempPath()
    {
        var path = Path.GetFullPath(Path.GetTempPath());
        return OperatingSystem.IsMacOS() &&
            (path.Equals("/var", StringComparison.Ordinal) || path.StartsWith("/var/", StringComparison.Ordinal))
            ? "/private" + path
            : path;
    }

    private static void AssertSafe(CaptureProvider logs)
    {
        Assert.NotEmpty(logs.Entries);
        Assert.All(logs.Entries.Where(e => e.Category.StartsWith("MailStencil", StringComparison.Ordinal)), e =>
        {
            Assert.DoesNotContain("SECRET", e.Message);
            Assert.DoesNotContain("SECRET", e.State);
            Assert.Null(e.Exception);
        });
    }

    private sealed class Reader : ITemplateReader
    {
        public Task<EmailTemplateSource?> GetAsync(TemplateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(request.Culture is not null ? null : new EmailTemplateSource(request, new("SECRET_SUBJECT", textBody: "SECRET_BODY")));
    }
    private sealed class Renderer : ITemplateRenderer
    {
        private readonly Exception? failure;
        public Renderer() { }
        internal Renderer(Exception failure) => this.failure = failure;
        public Task<RenderedEmailTemplate> RenderAsync<TModel>(EmailTemplateContent content, TModel model, CultureInfo culture, CancellationToken cancellationToken = default) where TModel : notnull =>
            failure is null ? Task.FromResult(new RenderedEmailTemplate("SECRET_RENDERED", textBody: "SECRET_RENDERED_BODY")) : Task.FromException<RenderedEmailTemplate>(failure);
    }
    private sealed class FailingService(Exception failure) : IEmailTemplateService
    {
        public Task<RenderedEmailTemplate> RenderAsync<TModel>(string name, TModel model, CancellationToken cancellationToken = default) where TModel : notnull => Task.FromException<RenderedEmailTemplate>(failure);
        public Task<RenderedEmailTemplate> RenderAsync<TModel>(TemplateRequest request, TModel model, CancellationToken cancellationToken = default) where TModel : notnull => Task.FromException<RenderedEmailTemplate>(failure);
    }
    private sealed class BlobTransport : HttpMessageHandler
    {
        internal int Calls;
        internal string Document = """{"formatVersion":1,"subject":"Hi {{ customer_name }}","htmlBody":"{{ customer_name | html.escape }}"}""";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Document)) };
            response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(Document);
            response.Headers.TryAddWithoutValidation("ETag", "\"test-etag\"");
            response.Content.Headers.LastModified = DateTimeOffset.UnixEpoch;
            response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            return Task.FromResult(response);
        }
    }
    private sealed class CaptureProvider : ILoggerProvider
    {
        internal readonly ConcurrentQueue<Entry> Entries = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);
        public void Dispose() { }
        internal sealed record Entry(string Category, LogLevel Level, EventId Event, string Message, string State, Exception? Exception);
        private sealed class CaptureLogger(string category, ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new(category, level, eventId, formatter(state, exception),
                    state is IEnumerable<KeyValuePair<string, object?>> values ? string.Join(";", values.Select(p => p.Key + "=" + p.Value)) : state?.ToString() ?? "", exception));
        }
    }
}
