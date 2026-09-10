using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailStencil.Core.Tests;

public sealed class FoundationTests
{
    [Fact]
    public void ContentAndOutputRequireSubjectAndAtLeastOneBody()
    {
        Assert.Throws<ArgumentNullException>(() => new EmailTemplateContent(null!, textBody: ""));
        Assert.Throws<ArgumentNullException>(() => new RenderedEmailTemplate(null!, textBody: ""));
        Assert.Throws<ArgumentException>(() => new EmailTemplateContent("subject"));
        Assert.Throws<ArgumentException>(() => new RenderedEmailTemplate("subject"));
        Assert.Equal("", new EmailTemplateContent("", htmlBody: "").HtmlBody);
        Assert.Null(new RenderedEmailTemplate("", textBody: "").HtmlBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void RequestRejectsBlankNames(string? name) =>
        Assert.ThrowsAny<ArgumentException>(() => new TemplateRequest(name!));

    [Fact]
    public void RequestHasValueIdentityAcrossCultureAndVersion()
    {
        Assert.Equal(new TemplateRequest("welcome"), new TemplateRequest("welcome", ""));
        Assert.Equal(CultureInfo.GetCultureInfo("it-IT").Name, new TemplateRequest("welcome", "it-IT").Culture);
        Assert.NotEqual(new TemplateRequest("welcome"), new TemplateRequest("Welcome"));
        Assert.NotEqual(new TemplateRequest("welcome"), new TemplateRequest("welcome", "it"));
        Assert.NotEqual(new TemplateRequest("welcome"), new TemplateRequest("welcome", version: "v1"));
        Assert.Throws<ArgumentException>(() => new TemplateRequest("welcome", version: " "));
        Assert.Throws<CultureNotFoundException>(() => new TemplateRequest("welcome", "!invalid!"));
    }

    [Fact]
    public void SourcePreservesSnapshotAndOptionalMetadata()
    {
        var request = new TemplateRequest("welcome");
        var content = new EmailTemplateContent("Hi", textBody: "Hello");
        var metadata = new TemplateMetadata { ETag = "opaque", Version = "v2", LastModified = DateTimeOffset.UtcNow };
        var source = new EmailTemplateSource(request, content, metadata);
        Assert.Same(request, source.Request);
        Assert.Same(content, source.Content);
        Assert.Same(metadata, source.Metadata);
        Assert.Null(new EmailTemplateSource(request, content).Metadata);
        Assert.Throws<ArgumentNullException>(() => new EmailTemplateSource(null!, content));
        Assert.Throws<ArgumentNullException>(() => new EmailTemplateSource(request, null!));
    }

    [Fact]
    public void RegistrationProvidesDefaultsAndDefersMissingDependenciesUntilServiceResolution()
    {
        var services = new ServiceCollection();
        Assert.Same(services, services.AddMailStencil());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.Equal("", provider.GetRequiredService<IOptions<MailStencilOptions>>().Value.DefaultCulture);
        Assert.Null(provider.GetService<ITemplateReader>());
        Assert.Null(provider.GetService<ITemplateRenderer>());
        Assert.Equal(TimeSpan.FromMinutes(5), provider.GetRequiredService<IOptions<MailStencilOptions>>().Value.CacheDuration);
        Assert.Single(services, d => d.ServiceType == typeof(IEmailTemplateService));
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IEmailTemplateService>());
    }

    [Fact]
    public void RepeatedRegistrationComposesConfigurationAndPreservesUserServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITemplateReader, MissingReader>();
        services.AddMailStencil(o => o.DefaultCulture = "it").AddMailStencil(o => o.DefaultCulture = "it-IT");
        using var provider = services.BuildServiceProvider();
        Assert.Equal("it-IT", provider.GetRequiredService<IOptions<MailStencilOptions>>().Value.DefaultCulture);
        Assert.IsType<MissingReader>(Assert.Single(provider.GetServices<ITemplateReader>()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("!invalid!")]
    public void InvalidOptionsFailWhenResolved(string? culture)
    {
        using var provider = new ServiceCollection().AddMailStencil(o => o.DefaultCulture = culture!).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<MailStencilOptions>>().Value);
    }

    [Fact]
    public void RegistrationRejectsNullCollection() =>
        Assert.Throws<ArgumentNullException>(() => MailStencilServiceCollectionExtensions.AddMailStencil(null!));

    private sealed class MissingReader : ITemplateReader
    {
        public Task<EmailTemplateSource?> GetAsync(TemplateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<EmailTemplateSource?>(null);
    }
}
