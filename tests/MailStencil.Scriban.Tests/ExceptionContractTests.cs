using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailStencil.Scriban.Tests;

public sealed class ExceptionContractTests : IDisposable
{
    private readonly ServiceProvider provider = new ServiceCollection().AddScribanRenderer().BuildServiceProvider();
    private ITemplateRenderer Renderer => provider.GetRequiredService<ITemplateRenderer>();
    private ITemplateValidator Validator => provider.GetRequiredService<ITemplateValidator>();
    public void Dispose() => provider.Dispose();

    [Theory]
    [InlineData("{{ if }}", "MSV001")]
    [InlineData("{{ missing }}", "MSV002")]
    [InlineData("{{ include 'file' }}", "MSV004")]
    public async Task StaticFailuresPreserveTheCompleteValidationResult(string invalid, string code)
    {
        var content = new EmailTemplateContent(invalid, invalid, invalid);
        var expected = await Validator.ValidateAsync<RenderingTests.ExampleModel>(content);
        var error = await Assert.ThrowsAsync<TemplateValidationException>(() => Renderer.RenderAsync(
            content, new RenderingTests.ExampleModel(), CultureInfo.InvariantCulture));
        Assert.False(error.ValidationResult.IsValid);
        Assert.Equal(expected.Diagnostics, error.ValidationResult.Diagnostics);
        Assert.Contains(error.ValidationResult.Diagnostics, d => d.Code == code);
        Assert.Equal(new[] { TemplateComponent.Subject, TemplateComponent.HtmlBody, TemplateComponent.TextBody },
            error.ValidationResult.Diagnostics.Select(d => d.Component).Distinct());
    }

    [Fact]
    public async Task UnsupportedModelProducesGeneralValidationFailure()
    {
        var content = new EmailTemplateContent("", textBody: "");
        var error = await Assert.ThrowsAsync<TemplateValidationException>(() => Renderer.RenderAsync(
            content, new ValidationTests.ObjectProperty(), CultureInfo.InvariantCulture));
        var diagnostic = Assert.Single(error.ValidationResult.Diagnostics);
        Assert.Equal(TemplateComponent.General, diagnostic.Component);
        Assert.Equal("MSV007", diagnostic.Code);
        Assert.Null(diagnostic.Span);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullContentAndCultureRemainArgumentNullExceptions(bool nullContent)
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Renderer.RenderAsync(
            nullContent ? null! : new EmailTemplateContent("", textBody: ""),
            new RenderingTests.ExampleModel(), nullContent ? CultureInfo.InvariantCulture : null!));
    }

    [Fact]
    public async Task HtmlValuesAreOnlyEscapedExplicitly()
    {
        var content = new EmailTemplateContent("", htmlBody: "{{ markup }}|{{ markup | html.escape }}");
        var validation = await Validator.ValidateAsync<RenderingTests.ExampleModel>(content);
        Assert.Empty(validation.Diagnostics);
        var result = await Renderer.RenderAsync(content, new RenderingTests.ExampleModel(), CultureInfo.InvariantCulture);
        Assert.Equal("<b>A & B</b>|&lt;b&gt;A &amp; B&lt;/b&gt;", result.HtmlBody);
    }
}
