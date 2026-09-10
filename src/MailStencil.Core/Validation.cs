namespace MailStencil;

/// <summary>Validates unsaved template content against a declared model type without executing it.</summary>
public interface ITemplateValidator
{
    /// <summary>Returns expected template problems as diagnostics. Does not evaluate model getters.</summary>
    Task<TemplateValidationResult> ValidateAsync<TModel>(EmailTemplateContent content,
        CancellationToken cancellationToken = default) where TModel : notnull;
}

/// <summary>The email component or whole-template scope of a diagnostic.</summary>
public enum TemplateComponent
{
    /// <summary>Email subject.</summary>
    Subject,
    /// <summary>HTML body.</summary>
    HtmlBody,
    /// <summary>Plain text body.</summary>
    TextBody,
    /// <summary>A whole-template or model diagnostic not associated with an individual component.</summary>
    General
}

/// <summary>The significance of a template diagnostic.</summary>
public enum TemplateDiagnosticSeverity
{
    /// <summary>A non-blocking concern.</summary>
    Warning,
    /// <summary>A problem preventing validation.</summary>
    Error
}

/// <summary>A location within one component. Offsets are zero-based UTF-16; line and column are one-based.</summary>
/// <param name="Offset">Start offset.</param>
/// <param name="Length">Span length.</param>
/// <param name="Line">Start line.</param>
/// <param name="Column">Start column.</param>
public sealed record TemplateSourceSpan(int Offset, int Length, int Line, int Column);

/// <summary>An engine-independent structured template problem.</summary>
/// <param name="Component">Affected component, or General for whole-template/model problems.</param>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="Code">Stable machine-readable code.</param>
/// <param name="Message">Human-readable explanation, not a stable identifier.</param>
/// <param name="Span">Source location when available.</param>
/// <param name="Member">Related variable or member when available.</param>
public sealed record TemplateDiagnostic(TemplateComponent Component, TemplateDiagnosticSeverity Severity,
    string Code, string Message, TemplateSourceSpan? Span = null, string? Member = null);

/// <summary>An immutable collection of validation diagnostics.</summary>
public sealed class TemplateValidationResult
{
    /// <summary>Copies diagnostics into a read-only snapshot.</summary>
    public TemplateValidationResult(IEnumerable<TemplateDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var copy = diagnostics.ToArray();
        if (copy.Any(d => d is null)) throw new ArgumentException("Diagnostics cannot contain null.", nameof(diagnostics));
        Diagnostics = Array.AsReadOnly(copy);
    }

    /// <summary>Gets diagnostics in component/source traversal order.</summary>
    public IReadOnlyList<TemplateDiagnostic> Diagnostics { get; }
    /// <summary>Gets whether no error diagnostics were found. Does not guarantee success for every model value.</summary>
    public bool IsValid => Diagnostics.All(d => d.Severity != TemplateDiagnosticSeverity.Error);
}

/// <summary>Template content or its declared model contract failed static validation.</summary>
/// <remarks>Contains the complete validation result, distinct from projection and execution failures.</remarks>
public sealed class TemplateValidationException : Exception
{
    /// <summary>Creates a validation failure preserving the supplied result and its structured diagnostics.</summary>
    /// <param name="validationResult">The static validation result.</param>
    /// <exception cref="ArgumentNullException">The result is null.</exception>
    public TemplateValidationException(TemplateValidationResult validationResult)
        : base("Template content failed static validation. See ValidationResult for structured diagnostics.")
    {
        ArgumentNullException.ThrowIfNull(validationResult);
        ValidationResult = validationResult;
    }

    /// <summary>Gets the complete, immutable static validation result.</summary>
    public TemplateValidationResult ValidationResult { get; }
}

/// <summary>An exceptional failure while projecting a model or executing a validated template.</summary>
public sealed class TemplateRenderingException : Exception
{
    /// <summary>Creates an execution failure with a structured diagnostic and optional underlying cause.</summary>
    public TemplateRenderingException(TemplateDiagnostic diagnostic, Exception? innerException = null)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).Message, innerException)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>Gets the component, code and location associated with the execution failure.</summary>
    public TemplateDiagnostic Diagnostic { get; }
}
