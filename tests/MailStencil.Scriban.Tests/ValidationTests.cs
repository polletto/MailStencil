using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailStencil.Scriban.Tests;

public sealed class ValidationTests : IDisposable
{
    private readonly ServiceProvider provider = new ServiceCollection().AddScribanRenderer().BuildServiceProvider();
    private ITemplateValidator Validator => provider.GetRequiredService<ITemplateValidator>();
    public void Dispose() => provider.Dispose();

    [Theory]
    [InlineData("{{ missing }}", "MSV002")]
    [InlineData("{{ CustomerName }}", "MSV002")]
    [InlineData("{{ customer.missing }}", "MSV003")]
    [InlineData("{{ customer.name.length }}", "MSV003")]
    [InlineData("{{ order.items.name }}", "MSV003")]
    [InlineData("{{ for item in order.items }}{{ item.missing }}{{ end }}", "MSV003")]
    [InlineData("{{ alias = customer; alias.missing }}", "MSV003")]
    [InlineData("{{ if is_vip }}{{ alias = customer }}{{ end }}{{ alias.name }}", "MSV002")]
    [InlineData("{{ for item in order.items }}{{ item.name }}{{ end }}{{ item.name }}", "MSV002")]
    [InlineData("{{ if is_vip }}{{ alias = customer }}{{ else }}{{ alias = order }}{{ end }}{{ alias.name }}", "MSV003")]
    [InlineData("{{ if }}", "MSV001")]
    [InlineData("{{ string.upcase }}", "MSV002")]
    [InlineData("{{ string.upcase customer_name order_number }}", "MSV005")]
    [InlineData("{{ customer[customer_name] }}", "MSV003")]
    [InlineData("{{ alias = customer; for i in 1..2; alias.name; alias = order; break; alias = customer; end }}", "MSV005")]
    [InlineData("{{ for item in order.items }}{{ for item in order.items }}{{ end }}{{ end }}", "MSV004")]
    [InlineData("{{ for item in order.items }}{{ else }}{{ alias = customer }}{{ end }}{{ alias.name }}", "MSV002")]
    public async Task FindsInvalidTemplatesWithoutExceptions(string text, string code)
    {
        var result = await Validator.ValidateAsync<RenderingTests.ExampleModel>(new EmailTemplateContent(text, textBody: ""));
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.Severity == TemplateDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task SuggestionsAreUsefulAndDeterministic()
    {
        var content = new EmailTemplateContent("{{ customer_namme }}", textBody: "");
        var a = await Validator.ValidateAsync<RenderingTests.ExampleModel>(content);
        var b = await Validator.ValidateAsync<RenderingTests.ExampleModel>(content);
        Assert.Equal(a.Diagnostics, b.Diagnostics);
        var diagnostic = Assert.Single(a.Diagnostics);
        Assert.Contains("Did you mean 'customer_name'?", diagnostic.Message);
        Assert.Equal("customer_namme", diagnostic.Member);
    }

    [Theory]
    [InlineData(TemplateComponent.Subject)]
    [InlineData(TemplateComponent.HtmlBody)]
    [InlineData(TemplateComponent.TextBody)]
    public async Task ReportsComponentAndSourceLocation(TemplateComponent component)
    {
        const string bad = "first\n{{ missing }}";
        var content = new EmailTemplateContent(component == TemplateComponent.Subject ? bad : "",
            component == TemplateComponent.HtmlBody ? bad : "", component == TemplateComponent.TextBody ? bad : "");
        var diagnostic = Assert.Single((await Validator.ValidateAsync<RenderingTests.ExampleModel>(content)).Diagnostics);
        Assert.Equal(component, diagnostic.Component);
        Assert.Equal(new TemplateSourceSpan(9, 7, 2, 4), diagnostic.Span);
    }

    [Fact]
    public async Task AggregatesAllComponents()
    {
        var result = await Validator.ValidateAsync<RenderingTests.ExampleModel>(new EmailTemplateContent("{{ a }}", "{{ b }}", "{{ c }}"));
        Assert.Equal([TemplateComponent.Subject, TemplateComponent.HtmlBody, TemplateComponent.TextBody], result.Diagnostics.Select(d => d.Component));
    }

    [Theory]
    [InlineData("{{ include 'secret' }}")]
    [InlineData("{{ include_join 'secret' }}")]
    [InlineData("{{ object.eval '1 + 1' }}")]
    [InlineData("{{ object.eval_template '{{ secret }}' }}")]
    [InlineData("{{ func f; f; end; f }}")]
    [InlineData("{{ while true }}x{{ end }}")]
    [InlineData("{{ alias = @string.upcase; alias customer_name }}")]
    [InlineData("{{ regex.match customer_name '.*' }}")]
    [InlineData("{{ array.add order.items 'x' }}")]
    [InlineData("{{ this }}")]
    [InlineData("{{ customer_name = 'changed' }}")]
    [InlineData("{{ for customer_name in order.items }}{{ end }}")]
    [InlineData("{{ string = customer }}")]
    public async Task DangerousOrUnsupportedConstructsFailClosed(string template)
    {
        var result = await Validator.ValidateAsync<RenderingTests.ExampleModel>(new EmailTemplateContent(template, textBody: ""));
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task SchemaInspectionDoesNotExecuteGetters()
    {
        Assert.True((await Validator.ValidateAsync<ThrowingGetter>(new EmailTemplateContent("{{ name }}", textBody: ""))).IsValid);
    }

    [Fact]
    public async Task UnsupportedClrTypesAndNamingCollisionsAreDiagnostics()
    {
        var content = new EmailTemplateContent("", textBody: "");
        Assert.Contains((await Validator.ValidateAsync<ObjectProperty>(content)).Diagnostics, d => d.Code == "MSV007" && d.Component == TemplateComponent.General);
        Assert.Contains((await Validator.ValidateAsync<DelegateProperty>(content)).Diagnostics, d => d.Code == "MSV007" && d.Component == TemplateComponent.General);
        Assert.Contains((await Validator.ValidateAsync<Collision>(content)).Diagnostics, d => d.Code == "MSV007" && d.Component == TemplateComponent.General);
        Assert.Contains((await Validator.ValidateAsync<Reserved>(content)).Diagnostics, d => d.Code == "MSV007" && d.Component == TemplateComponent.General);
        Assert.Contains((await Validator.ValidateAsync<DictionaryProperty>(content)).Diagnostics, d => d.Code == "MSV007" && d.Component == TemplateComponent.General);
        Assert.Contains((await Validator.ValidateAsync<TypeProperty>(content)).Diagnostics, d => d.Code == "MSV007" && d.Component == TemplateComponent.General);
    }

    [Theory]
    [InlineData(TemplateComponent.Subject)]
    [InlineData(TemplateComponent.HtmlBody)]
    [InlineData(TemplateComponent.TextBody)]
    public async Task SyntaxDiagnosticsIdentifyEachComponent(TemplateComponent component)
    {
        const string bad = "{{ if }}";
        var content = new EmailTemplateContent(component == TemplateComponent.Subject ? bad : "",
            component == TemplateComponent.HtmlBody ? bad : "", component == TemplateComponent.TextBody ? bad : "");
        var result = await Validator.ValidateAsync<RenderingTests.ExampleModel>(content);
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d => { Assert.Equal(component, d.Component); Assert.Equal("MSV001", d.Code); Assert.NotNull(d.Span); });
    }

    public sealed class ThrowingGetter { public string Name => throw new InvalidOperationException("getter executed"); }
    public sealed class ObjectProperty { public object Value => new(); }
    public sealed class DelegateProperty { public Action Callback => () => { }; }
    public sealed class Collision { public string Name => ""; public string name => ""; }
    public sealed class Reserved { public string String => ""; }
    public sealed class DictionaryProperty { public Dictionary<string, string> Values => []; }
    public sealed class TypeProperty { public Type Type => typeof(string); }
}
