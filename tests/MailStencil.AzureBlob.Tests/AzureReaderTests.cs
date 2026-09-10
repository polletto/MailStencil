using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MailStencil.AzureBlob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailStencil.AzureBlob.Tests;

public sealed class AzureReaderTests
{
    internal const string Valid = """{"formatVersion":1,"subject":"Hello","htmlBody":"<b>世界</b>","textBody":"Hi"}""";

    internal static ServiceProvider Register(BlobServiceClient client, Action<AzureBlobTemplateOptions>? configure = null)
    {
        var services = new ServiceCollection().AddSingleton(client);
        services.AddAzureBlobTemplateReader(o => { o.ContainerName = "templates"; configure?.Invoke(o); });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Theory]
    [InlineData(null, "", "welcome/default/template.json")]
    [InlineData("it-IT", "mailstencil", "mailstencil/welcome/it-IT/template.json")]
    [InlineData("it", "mailstencil/", "mailstencil/welcome/it/template.json")]
    [InlineData(null, "/mailstencil//nested/", "mailstencil/nested/welcome/default/template.json")]
    public async Task ExactIdentityAndMetadata(string? culture, string prefix, string path)
    {
        var fake = new FakeService();
        using var services = Register(fake, o => o.Prefix = prefix);
        var request = new TemplateRequest("welcome", culture);
        var old = CultureInfo.CurrentCulture;
        var oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var source = Assert.IsType<EmailTemplateSource>(await services.GetRequiredService<ITemplateReader>().GetAsync(request));
            Assert.Same(request, source.Request);
            Assert.Equal("Hello", source.Content.Subject);
            Assert.Equal("<b>世界</b>", source.Content.HtmlBody);
            Assert.Equal("Hi", source.Content.TextBody);
            Assert.Equal("\"etag-1\"", source.Metadata?.ETag);
            Assert.Equal(FakeService.Modified, source.Metadata?.LastModified);
            Assert.Null(source.Metadata?.Version);
            Assert.Equal(path, Assert.Single(fake.Paths));
            Assert.Equal("templates", fake.ContainerName);
            Assert.All(fake.Streams, s => Assert.True(s.Disposed));
            Assert.All(fake.Responses, r => Assert.True(r.Disposed));
        }
        finally { CultureInfo.CurrentCulture = old; CultureInfo.CurrentUICulture = oldUi; }
    }

    [Theory]
    [InlineData("{\"formatVersion\":1,\"subject\":\"\",\"htmlBody\":\"\"}", false, "", null)]
    [InlineData("{\"formatVersion\":1,\"subject\":\"😀\",\"textBody\":\"é\"}", true, null, "é")]
    [InlineData("{\"formatVersion\":1,\"subject\":\"x\",\"htmlBody\":null,\"textBody\":\"\"}", false, null, "")]
    [InlineData(Valid, true, "<b>世界</b>", "Hi")]
    public async Task BodyShapesAndBom(string json, bool bom, string? html, string? text)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var fake = new FakeService { Bytes = bom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes };
        using var services = Register(fake);
        var source = Assert.IsType<EmailTemplateSource>(await services.GetRequiredService<ITemplateReader>().GetAsync(new("welcome")));
        Assert.Equal(html, source.Content.HtmlBody);
        Assert.Equal(text, source.Content.TextBody);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"subject\":\"x\",\"textBody\":\"x\"}")]
    [InlineData("{\"formatVersion\":2,\"subject\":\"x\",\"textBody\":\"x\"}")]
    [InlineData("{\"formatVersion\":\"1\",\"subject\":\"x\",\"textBody\":\"x\"}")]
    [InlineData("{\"formatVersion\":1,\"textBody\":\"x\"}")]
    [InlineData("{\"formatVersion\":1,\"subject\":null,\"textBody\":\"x\"}")]
    [InlineData("{\"formatVersion\":1,\"subject\":5,\"textBody\":\"x\"}")]
    [InlineData("{\"formatVersion\":1,\"subject\":\"x\"}")]
    [InlineData("{\"formatVersion\":1,\"subject\":\"x\",\"htmlBody\":null,\"textBody\":null}")]
    [InlineData("{\"formatVersion\":1,\"subject\":\"x\",\"textBody\":false}")]
    [InlineData("{\"formatVersion\":1,\"subject\":\"x\",\"htmlBody\":[]}")]
    [InlineData("{\"formatVersion\":1,\"subject\":\"x\",\"htmlBdy\":\"x\",\"textBody\":\"x\"}")]
    [InlineData("{\"formatVersion\":1,\"subject\":\"x\",\"subject\":\"y\",\"textBody\":\"x\"}")]
    [InlineData("{\"formatVersion\":1,\"subject\":\"x\",\"textBody\":\"x\",}")]
    [InlineData("{/*comment*/\"formatVersion\":1,\"subject\":\"x\",\"textBody\":\"x\"}")]
    [InlineData("{\"formatVersion\":1,\"subject\":\"\\uD800\",\"textBody\":\"x\"}")]
    [InlineData("{\"formatVersion\":1,\"subject\":\"x\",\"su\\u0062ject\":\"y\",\"textBody\":\"x\"}")]
    public async Task RejectsMalformedDocuments(string json)
    {
        var fake = new FakeService { Bytes = Encoding.UTF8.GetBytes(json) };
        using var services = Register(fake);
        var request = new TemplateRequest("welcome");
        var error = await Assert.ThrowsAsync<AzureBlobTemplateException>(() => services.GetRequiredService<ITemplateReader>().GetAsync(request));
        Assert.Same(request, error.Request);
        Assert.All(fake.Streams, s => Assert.True(s.Disposed));
        Assert.All(fake.Responses, r => Assert.True(r.Disposed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RejectsInvalidEncodings(int kind)
    {
        byte[] bytes = kind switch
        {
            0 => [.. Encoding.UTF8.GetBytes("{\"subject\":\""), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\"}")],
            1 => Encoding.Unicode.GetBytes(Valid),
            _ => [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Valid)]
        };
        using var services = Register(new FakeService { Bytes = bytes });
        await Assert.ThrowsAsync<AzureBlobTemplateException>(() => services.GetRequiredService<ITemplateReader>().GetAsync(new("welcome")));
    }

    [Theory]
    [InlineData(404, "BlobNotFound", true)]
    [InlineData(404, "ContainerNotFound", false)]
    [InlineData(404, "ResourceNotFound", false)]
    [InlineData(401, "AuthenticationFailed", false)]
    [InlineData(403, "AuthorizationFailure", false)]
    [InlineData(409, "Conflict", false)]
    [InlineData(429, "TooManyRequests", false)]
    [InlineData(500, "InternalError", false)]
    [InlineData(503, "ServerBusy", false)]
    public async Task OnlyExactBlobAbsenceIsNull(int status, string code, bool missing)
    {
        var failure = new RequestFailedException(status, "service error", code, null);
        var fake = new FakeService { Failure = failure };
        using var services = Register(fake);
        var reader = services.GetRequiredService<ITemplateReader>();
        if (missing) Assert.Null(await reader.GetAsync(new("welcome", "it-IT")));
        else Assert.Same(failure, await Assert.ThrowsAsync<RequestFailedException>(() => reader.GetAsync(new("welcome", "it-IT"))));
        Assert.Single(fake.Paths);
    }

    [Theory]
    [InlineData("../welcome")]
    [InlineData("folder/welcome")]
    [InlineData("folder\\welcome")]
    [InlineData("x%2fy")]
    [InlineData("é")]
    [InlineData("_welcome")]
    [InlineData("a:b")]
    public async Task UnsafeNamesRejectedBeforeIo(string name)
    {
        var fake = new FakeService();
        using var services = Register(fake);
        await Assert.ThrowsAsync<ArgumentException>(() => services.GetRequiredService<ITemplateReader>().GetAsync(new(name)));
        Assert.Empty(fake.Paths);
    }

    [Fact]
    public async Task NullVersionAndNameLimits()
    {
        var fake = new FakeService();
        using var services = Register(fake);
        var reader = services.GetRequiredService<ITemplateReader>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.GetAsync(null!));
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.GetAsync(new("welcome", version: "v3")));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.GetAsync(new(new string('x', 129))));
        Assert.Empty(fake.Paths);
        Assert.NotNull(await reader.GetAsync(new(new string('x', 128))));
        Assert.Single(fake.Paths);
    }

    [Theory]
    [InlineData(1000, 100, 0)]
    [InlineData(1, 100, 101)]
    [InlineData(300, 400, 200)]
    public async Task BoundsLengthAndDisposes(long reported, int limit, int actual)
    {
        var fake = new FakeService { Bytes = new byte[actual], ReportedLength = reported };
        using var services = Register(fake, o => o.MaxTemplateBlobSize = limit);
        await Assert.ThrowsAsync<AzureBlobTemplateException>(() => services.GetRequiredService<ITemplateReader>().GetAsync(new("welcome")));
        var stream = Assert.Single(fake.Streams);
        Assert.True(stream.Disposed);
        Assert.InRange(stream.BytesRead, 0, limit + 1);
        if (reported > limit) Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task ExactSizeAccepted()
    {
        var fake = new FakeService();
        using var services = Register(fake, o => o.MaxTemplateBlobSize = fake.Bytes.Length);
        Assert.NotNull(await services.GetRequiredService<ITemplateReader>().GetAsync(new("welcome")));
    }

    [Fact]
    public async Task CancellationBeforeAndDuringStreamRead()
    {
        var fake = new FakeService();
        using var services = Register(fake);
        var reader = services.GetRequiredService<ITemplateReader>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.GetAsync(new("welcome"), cancelled.Token));
        Assert.Empty(fake.Paths);
        using var during = new CancellationTokenSource();
        fake.OnRead = during.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.GetAsync(new("welcome"), during.Token));
        Assert.Equal(during.Token, Assert.Single(fake.Tokens));
        Assert.True(Assert.Single(fake.Streams).Disposed);
    }

    [Fact]
    public async Task NetworkAndDownloadCancellationPropagate()
    {
        var fake = new FakeService();
        using var services = Register(fake);
        foreach (var error in new Exception[] { new HttpRequestException("network"), new OperationCanceledException() })
        {
            fake.Failure = error;
            Assert.Same(error, await Record.ExceptionAsync(() => services.GetRequiredService<ITemplateReader>().GetAsync(new("welcome"))));
        }
    }

    [Fact]
    public async Task ConcurrentReadsRemainIndependentAndDirectReadsAreNotCached()
    {
        var fake = new FakeService();
        using var services = Register(fake);
        var reader = services.GetRequiredService<ITemplateReader>();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => reader.GetAsync(new(i < 16 ? "same" : "name" + i)))));
        Assert.All(results, r => Assert.Equal("Hello", r?.Content.Subject));
        Assert.Equal(32, fake.Paths.Count);
        Assert.Equal(16, fake.Paths.Count(p => p == "same/default/template.json"));
        Assert.All(fake.Streams, s => Assert.True(s.Disposed));
    }

    [Theory]
    [InlineData("", "", 1)]
    [InlineData("ABc", "", 1)]
    [InlineData("a--b", "", 1)]
    [InlineData("ab", "", 1)]
    [InlineData("abc", "../bad", 1)]
    [InlineData("abc", "bad\\prefix", 1)]
    [InlineData("abc", "", 0)]
    [InlineData("abc", "", 16777217)]
    [InlineData("abc", null, 1)]
    public void InvalidOptionsFailResolution(string container, string? prefix, int size)
    {
        using var services = Register(new FakeService(), o => { o.ContainerName = container; o.Prefix = prefix!; o.MaxTemplateBlobSize = size; });
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<ITemplateReader>());
    }

    [Fact]
    public async Task RegistrationAndOptionsSnapshot()
    {
        var fake = new FakeService();
        using var services = Register(fake);
        var reader = services.GetRequiredService<ITemplateReader>();
        Assert.Same(reader, services.GetRequiredService<ITemplateReader>());
        var options = services.GetRequiredService<IOptions<AzureBlobTemplateOptions>>().Value;
        Assert.Equal(4 * 1024 * 1024, options.MaxTemplateBlobSize);
        options.Prefix = "other";
        options.ContainerName = "other-container";
        options.MaxTemplateBlobSize = 1;
        await reader.GetAsync(new("welcome"));
        Assert.Equal("welcome/default/template.json", Assert.Single(fake.Paths));
        Assert.Equal("templates", fake.ContainerName);
        var registrations = new ServiceCollection().AddAzureBlobTemplateReader(o => o.ContainerName = "templates");
        Assert.Throws<InvalidOperationException>(() => registrations.AddAzureBlobTemplateReader(_ => { }));
        using var missingClient = registrations.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => missingClient.GetRequiredService<ITemplateReader>());
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAzureBlobTemplateReader(null!));
        Assert.Throws<ArgumentNullException>(() => MailStencilAzureBlobServiceCollectionExtensions.AddAzureBlobTemplateReader(null!, _ => { }));
    }

    [Fact]
    public void ExistingReaderAndExceptionArguments()
    {
        var registrations = new ServiceCollection().AddSingleton<ITemplateReader>(_ => throw new InvalidOperationException());
        Assert.Throws<InvalidOperationException>(() => registrations.AddAzureBlobTemplateReader(_ => { }));
        Assert.Throws<ArgumentNullException>(() => new AzureBlobTemplateException(null!, "error"));
        Assert.Throws<ArgumentNullException>(() => new AzureBlobTemplateException(new("welcome"), null!));
    }

    [Fact]
    public async Task RuntimeComposesFallbackCacheAndScribanWithoutProviderSpecialCases()
    {
        var fake = new FakeService
        {
            MissingPath = "welcome/it-IT/template.json",
            Bytes = Encoding.UTF8.GetBytes("""{"formatVersion":1,"subject":"Hi {{ name }}","htmlBody":"{{ name | html.escape }}"}""")
        };
        using var services = new ServiceCollection().AddSingleton<BlobServiceClient>(fake)
            .AddMailStencil().AddScribanRenderer().AddAzureBlobTemplateReader(o => o.ContainerName = "templates")
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var service = services.GetRequiredService<IEmailTemplateService>();
        var request = new TemplateRequest("welcome", "it-IT");
        var first = await service.RenderAsync(request, new AzuriteTests.Model("Ada & friends"));
        Assert.Equal("Hi Ada & friends", first.Subject);
        Assert.Equal("Ada &amp; friends", first.HtmlBody);
        fake.Bytes = Encoding.UTF8.GetBytes(Valid);
        Assert.Equal("Hi Grace", (await service.RenderAsync(request, new AzuriteTests.Model("Grace"))).Subject);
        Assert.Equal(new[] { "welcome/it-IT/template.json", "welcome/it/template.json", "welcome/it-IT/template.json" }, fake.Paths);
        fake.MissingPath = null;
        Assert.Equal("Hello", (await service.RenderAsync(request, new AzuriteTests.Model("Ada"))).Subject);
    }
}

internal sealed class FakeService : BlobServiceClient
{
    internal static readonly DateTimeOffset Modified = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
    internal byte[] Bytes { get; set; } = Encoding.UTF8.GetBytes(AzureReaderTests.Valid);
    internal long? ReportedLength { get; set; }
    internal Exception? Failure { get; set; }
    internal string? MissingPath { get; set; }
    internal Action? OnRead { get; set; }
    internal string? ContainerName { get; private set; }
    internal ConcurrentQueue<string> Paths { get; } = new();
    internal ConcurrentQueue<CancellationToken> Tokens { get; } = new();
    internal ConcurrentQueue<TrackingStream> Streams { get; } = new();
    internal ConcurrentQueue<TestResponse> Responses { get; } = new();
    public override BlobContainerClient GetBlobContainerClient(string blobContainerName)
    {
        ContainerName = blobContainerName;
        return new Container(this);
    }
    private sealed class Container(FakeService service) : BlobContainerClient
    {
        public override BlobClient GetBlobClient(string blobName) => new Blob(service, blobName);
    }
    private sealed class Blob(FakeService service, string name) : BlobClient
    {
        public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions? options = null, CancellationToken cancellationToken = default)
        {
            service.Paths.Enqueue(name);
            service.Tokens.Enqueue(cancellationToken);
            if (name == service.MissingPath)
                return Task.FromException<Response<BlobDownloadStreamingResult>>(new RequestFailedException(404, "Missing", "BlobNotFound", null));
            if (service.Failure is { } failure) return Task.FromException<Response<BlobDownloadStreamingResult>>(failure);
            var stream = new TrackingStream(service.Bytes, service.OnRead);
            var response = new TestResponse();
            service.Streams.Enqueue(stream);
            service.Responses.Enqueue(response);
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(stream,
                BlobsModelFactory.BlobDownloadDetails(contentLength: service.ReportedLength ?? service.Bytes.Length,
                    lastModified: Modified, eTag: new ETag("\"etag-1\""), versionId: "native-version-not-logical")), response));
        }
    }
}

internal sealed class TrackingStream(byte[] bytes, Action? onRead) : MemoryStream(bytes)
{
    internal bool Disposed { get; private set; }
    internal int BytesRead { get; private set; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        onRead?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var count = await base.ReadAsync(buffer, cancellationToken);
        BytesRead += count;
        return count;
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}

internal sealed class TestResponse : Response
{
    internal bool Disposed { get; private set; }
    public override int Status => 200;
    public override string ReasonPhrase => "OK";
    public override Stream? ContentStream { get; set; }
    public override string ClientRequestId { get; set; } = "test";
    public override void Dispose() => Disposed = true;
    protected override bool ContainsHeader(string name) => false;
    protected override IEnumerable<Azure.Core.HttpHeader> EnumerateHeaders() => [];
    protected override bool TryGetHeader(string name, out string value) { value = null!; return false; }
    protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = null!; return false; }
}

