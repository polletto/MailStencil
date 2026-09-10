using Xunit;

namespace MailStencil.Core.Tests;

public sealed class ValidationExceptionTests
{
    [Fact]
    public void ExceptionPreservesResultAndDiagnostics()
    {
        var diagnostic = new TemplateDiagnostic(TemplateComponent.General, TemplateDiagnosticSeverity.Error,
            "MSV007", "Unsupported contract.");
        var result = new TemplateValidationResult([diagnostic]);
        var error = new TemplateValidationException(result);
        Assert.Same(result, error.ValidationResult);
        Assert.Same(diagnostic, Assert.Single(error.ValidationResult.Diagnostics));
    }

    [Fact]
    public void NullResultIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new TemplateValidationException(null!));

    [Fact]
    public void GeneralPreservesExistingComponentNumericValues()
    {
        Assert.Equal(0, (int)TemplateComponent.Subject);
        Assert.Equal(1, (int)TemplateComponent.HtmlBody);
        Assert.Equal(2, (int)TemplateComponent.TextBody);
        Assert.Equal(3, (int)TemplateComponent.General);
    }
}
