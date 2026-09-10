using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MailStencil.AzureBlob;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailStencil.AzureBlob.Tests;

// Opt-in, local emulator only. Once enabled, connection/setup errors fail rather than silently skip.
public sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAILSTENCIL_AZURITE") != "1")
            Skip = "Set MAILSTENCIL_AZURITE=1 with Azurite Blob listening on localhost:10000.";
    }
}

public sealed class AzuriteTheoryAttribute : TheoryAttribute
{
    public AzuriteTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAILSTENCIL_AZURITE") != "1")
            Skip = "Set MAILSTENCIL_AZURITE=1 with Azurite Blob listening on localhost:10000.";
    }
}

[Trait("Category", "Azurite")]
public sealed class AzuriteTests : IAsyncLifetime
{
    private readonly BlobServiceClient client = new("UseDevelopmentStorage=true", new BlobClientOptions(BlobClientOptions.ServiceVersion.V2021_12_02)
    {
        Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(10) }
    });
    private BlobContainerClient container = null!;

    public async Task InitializeAsync()
    {
        container = client.GetBlobContainerClient("mailstencil-tests-" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
    }

    public async Task DisposeAsync() => await container.DeleteIfExistsAsync();

    private Task Upload(string name, string json, string type = "application/json", bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        return container.GetBlobClient("mailstencil/" + name + "/template.json").UploadAsync(BinaryData.FromBytes(bytes),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = type } });
    }

    private ServiceProvider Services(bool runtime = false)
    {
        var services = new ServiceCollection().AddSingleton(client);
        if (runtime) services.AddMailStencil().AddScribanRenderer();
        services.AddAzureBlobTemplateReader(o => { o.ContainerName = container.Name; o.Prefix = "mailstencil/"; });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [AzuriteTheory]
    [InlineData("application/json", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("application/json", true)]
    [InlineData("application/octet-stream", true)]
    public async Task DownloadsUtf8AndMetadataRegardlessOfContentType(string type, bool bom)
    {
        await Upload("welcome/default", AzureReaderTests.Valid, type, bom);
        using var services = Services();
        var source = Assert.IsType<EmailTemplateSource>(await services.GetRequiredService<ITemplateReader>().GetAsync(new("welcome")));
        Assert.Equal("Hello", source.Content.Subject);
        Assert.Equal("<b>世界</b>", source.Content.HtmlBody);
        Assert.Equal("Hi", source.Content.TextBody);
        var properties = (await container.GetBlobClient("mailstencil/welcome/default/template.json").GetPropertiesAsync()).Value;
        Assert.Equal(properties.ETag.ToString(), source.Metadata?.ETag);
        Assert.Equal(properties.LastModified, source.Metadata?.LastModified);
        Assert.Null(source.Metadata?.Version);
    }

    [AzuriteFact]
    public async Task ExactCultureAndCaseNeverFallBack()
    {
        await Upload("welcome/it", AzureReaderTests.Valid);
        await Upload("welcome/default", AzureReaderTests.Valid);
        using var services = Services();
        var reader = services.GetRequiredService<ITemplateReader>();
        Assert.Null(await reader.GetAsync(new("welcome", "it-IT")));
        Assert.Null(await reader.GetAsync(new("Welcome")));
        Assert.NotNull(await reader.GetAsync(new("welcome")));
        await Upload("welcome/it-IT", AzureReaderTests.Valid);
        Assert.NotNull(await reader.GetAsync(new("welcome", "it-IT")));
        Assert.Null(await reader.GetAsync(new("missing")));
    }

    [AzuriteFact]
    public async Task MissingContainerIsAnOperationalError()
    {
        await container.DeleteAsync();
        using var services = Services();
        var error = await Assert.ThrowsAsync<RequestFailedException>(() => services.GetRequiredService<ITemplateReader>().GetAsync(new("welcome")));
        Assert.Equal("ContainerNotFound", error.ErrorCode);
    }

    [AzuriteTheory]
    [InlineData("{bad json")]
    [InlineData("{\"formatVersion\":2,\"subject\":\"x\",\"textBody\":\"x\"}")]
    public async Task MalformedBlobsAreProviderErrors(string json)
    {
        await Upload("welcome/default", json);
        using var services = Services();
        await Assert.ThrowsAsync<AzureBlobTemplateException>(() => services.GetRequiredService<ITemplateReader>().GetAsync(new("welcome")));
    }

    [AzuriteFact]
    public async Task ConcurrentSameAndDifferentBlobs()
    {
        await Upload("first/default", AzureReaderTests.Valid);
        await Upload("second/default", AzureReaderTests.Valid);
        using var services = Services();
        var reader = services.GetRequiredService<ITemplateReader>();
        var sources = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => reader.GetAsync(new(i % 2 == 0 ? "first" : "second"))));
        Assert.All(sources, s => Assert.Equal("Hello", s?.Content.Subject));
    }

    [AzuriteFact]
    public async Task RuntimeFallbackPositiveCacheAndExplicitEscaping()
    {
        const string json = """{"formatVersion":1,"subject":"Hello {{ name }}","htmlBody":"<p>{{ name | html.escape }}</p>"}""";
        await Upload("welcome/it", json);
        using var services = Services(runtime: true);
        var service = services.GetRequiredService<IEmailTemplateService>();
        var request = new TemplateRequest("welcome", "it-IT");
        var rendered = await service.RenderAsync(request, new Model("Ada & friends"));
        Assert.Equal("Hello Ada & friends", rendered.Subject);
        Assert.Equal("<p>Ada &amp; friends</p>", rendered.HtmlBody);
        await Upload("welcome/it", AzureReaderTests.Valid);
        Assert.Equal("Hello Grace", (await service.RenderAsync(request, new Model("Grace"))).Subject);
        Assert.Equal("Hello", (await services.GetRequiredService<ITemplateReader>().GetAsync(new("welcome", "it")))?.Content.Subject);
        await Upload("welcome/it-IT", AzureReaderTests.Valid);
        Assert.Equal("Hello", (await service.RenderAsync(request, new Model("Ada"))).Subject);
    }

    [AzuriteFact]
    public async Task CancellationAndLogicalVersionRejection()
    {
        using var services = Services();
        var reader = services.GetRequiredService<ITemplateReader>();
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.GetAsync(new("welcome", version: "v1")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.GetAsync(new("welcome"), cancellation.Token));
    }

    public sealed record Model(string Name);
}
