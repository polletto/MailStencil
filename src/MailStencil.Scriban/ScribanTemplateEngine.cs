using System.Globalization;
using System.Text;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace MailStencil.Scriban;

internal sealed class ScribanTemplateEngine : ITemplateRenderer, ITemplateValidator
{
    public Task<TemplateValidationResult> ValidateAsync<TModel>(EmailTemplateContent content,
        CancellationToken cancellationToken = default) where TModel : notnull
    {
        ArgumentNullException.ThrowIfNull(content);
        var analysis = Analyze<TModel>(content, cancellationToken);
        return Task.FromResult(new TemplateValidationResult(analysis.Diagnostics));
    }

    public async Task<RenderedEmailTemplate> RenderAsync<TModel>(EmailTemplateContent content, TModel model,
        CultureInfo culture, CancellationToken cancellationToken = default) where TModel : notnull
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(culture);
        var analysis = Analyze<TModel>(content, cancellationToken);
        if (analysis.Diagnostics.Any(d => d.Severity == TemplateDiagnosticSeverity.Error))
            throw new TemplateValidationException(new TemplateValidationResult(analysis.Diagnostics));

        ScriptObject projected;
        try
        {
            var nodes = 0;
            projected = (ScriptObject)analysis.Schema.Project(analysis.Root!, model, ref nodes)!;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new TemplateRenderingException(new(TemplateComponent.General, TemplateDiagnosticSeverity.Error,
                "MSR002", "Model projection failed before rendering any component."), ex);
        }

        var output = new Dictionary<TemplateComponent, string>();
        foreach (var (component, template) in analysis.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new TemplateContext(SafeBuiltins.Create())
            {
                StrictVariables = true,
                EnableRelaxedTargetAccess = false,
                EnableRelaxedMemberAccess = false,
                EnableRelaxedFunctionAccess = false,
                EnableRelaxedIndexerAccess = false,
                EnableNullIndexer = false,
                EnableBreakAndContinueAsReturnOutsideLoop = false,
                ErrorForStatementFunctionAsExpression = true,
                LoopLimit = Limits.Loop,
                RecursiveLimit = Limits.ModelDepth,
                ObjectRecursionLimit = Limits.ModelDepth,
                LimitToString = Limits.OutputLength,
                RegexTimeOut = TimeSpan.FromMilliseconds(100),
                CancellationToken = cancellationToken,
                MemberFilter = _ => false,
                TemplateLoader = null,
                AutoIndent = false
            };
            context.PushCulture(CultureInfo.ReadOnly((CultureInfo)culture.Clone()));
            context.PushGlobal(projected);
            context.PushGlobal(new ScriptObject()); // Per-component writable local variables.
            var writer = new BoundedOutput(cancellationToken);
            context.PushOutput(writer);
            try
            {
                await template.RenderAsync(context).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                output.Add(component, writer.ToString());
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex)
            {
                throw new TemplateRenderingException(new(component, TemplateDiagnosticSeverity.Error, "MSR001",
                    "Template execution failed (invalid value or execution limit).",
                    ex is ScriptRuntimeException script ? AstValidator.Location(script.Span) : null), ex);
            }
        }
        return new RenderedEmailTemplate(output[TemplateComponent.Subject],
            output.GetValueOrDefault(TemplateComponent.HtmlBody), output.GetValueOrDefault(TemplateComponent.TextBody));
    }

    private static Analysis Analyze<TModel>(EmailTemplateContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var analysis = new Analysis(new ModelSchema(cancellationToken));
        try
        {
            analysis.Root = analysis.Schema.Build(typeof(TModel));
            if (analysis.Root.Kind != ShapeKind.Object)
                throw new NotSupportedException("The root model must be a property-based data contract.");
            if (analysis.Root.Members.Keys.Any(SafeBuiltins.IsReserved))
                throw new NotSupportedException("A root model member conflicts with a reserved builtin or loop name.");
        }
        catch (NotSupportedException ex)
        {
            analysis.Diagnostics.Add(new(TemplateComponent.General, TemplateDiagnosticSeverity.Error, "MSV007", ex.Message));
        }

        foreach (var (component, text) in new[]
                 {
                     (TemplateComponent.Subject, content.Subject), (TemplateComponent.HtmlBody, content.HtmlBody),
                     (TemplateComponent.TextBody, content.TextBody)
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text is null) continue;
            if (text.Length > Limits.TemplateLength)
            {
                analysis.Diagnostics.Add(new(component, TemplateDiagnosticSeverity.Error, "MSV006", "Component exceeds the template size limit."));
                continue;
            }
            var template = Template.Parse(text, component.ToString(), new ParserOptions { ExpressionDepthLimit = Limits.AstDepth });
            cancellationToken.ThrowIfCancellationRequested();
            if (template.HasErrors)
            {
                foreach (var message in template.Messages)
                    analysis.Diagnostics.Add(new(component, TemplateDiagnosticSeverity.Error, "MSV001", message.Message,
                        AstValidator.Location(message.Span)));
                continue;
            }
            if (analysis.Root is not null)
                new AstValidator(analysis.Root, component, analysis.Diagnostics, cancellationToken).Visit(template.Page);
            analysis.Parts.Add((component, template));
        }
        return analysis;
    }

    private sealed class Analysis(ModelSchema schema)
    {
        internal ModelSchema Schema { get; } = schema;
        internal ModelShape? Root { get; set; }
        internal List<TemplateDiagnostic> Diagnostics { get; } = [];
        internal List<(TemplateComponent Component, Template Template)> Parts { get; } = [];
    }

    private sealed class BoundedOutput(CancellationToken cancellationToken) : IScriptOutput
    {
        private readonly StringBuilder builder = new();
        public void Write(string text, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count > Limits.OutputLength - builder.Length)
                throw new InvalidOperationException("Rendered output exceeds the size limit.");
            builder.Append(text, offset, count);
        }

        public ValueTask WriteAsync(string text, int offset, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Write(text, offset, count);
            return ValueTask.CompletedTask;
        }

        public override string ToString() => builder.ToString();
    }
}
