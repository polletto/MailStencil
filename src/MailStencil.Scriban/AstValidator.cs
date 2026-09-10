using Scriban.Parsing;
using Scriban.Syntax;

namespace MailStencil.Scriban;

// A conservative static checker for the supported Scriban subset. No template execution or
// sample model values are used to discover members. Unknown AST constructs fail closed.
internal sealed class AstValidator(ModelShape root, TemplateComponent component,
    List<TemplateDiagnostic> diagnostics, CancellationToken cancellationToken)
{
    private Dictionary<string, ModelShape> locals = new(StringComparer.Ordinal);
    private int depth;
    private int loops;

    internal void Visit(ScriptNode? node)
    {
        if (node is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (++depth > Limits.AstDepth)
        {
            Error(node, "MSV006", "Template AST exceeds the depth limit.");
            depth--;
            return;
        }
        try
        {
            switch (node)
            {
                case ScriptPage page: Visit(page.Body); break;
                case ScriptBlockStatement block:
                    foreach (var statement in block.Statements) Visit(statement);
                    break;
                case ScriptRawStatement: break;
                case ScriptEscapeStatement: break;
                case ScriptEndStatement: break;
                case ScriptExpressionStatement statement:
                    if (statement.Expression is ScriptAssignExpression assign) Assign(assign);
                    else Infer(statement.Expression);
                    break;
                case ScriptIfStatement condition: If(condition); break;
                case ScriptElseStatement otherwise: Visit(otherwise.Body); break;
                case ScriptForStatement loop when loop.GetType() == typeof(ScriptForStatement): For(loop); break;
                case ScriptBreakStatement or ScriptContinueStatement when loops > 0: break;
                default: Error(node, "MSV004", "This Scriban construct is not supported by MailStencil."); break;
            }
        }
        finally { depth--; }
    }

    private void Assign(ScriptAssignExpression assignment)
    {
        var value = Infer(assignment.Value);
        if (assignment.Target is not ScriptVariable variable || SafeBuiltins.IsReserved(variable.Name) ||
            variable.Scope == ScriptVariableScope.Global && root.Members.ContainsKey(variable.Name))
        {
            Error(assignment, "MSV004", "Only local variable assignment is allowed; model members and builtins are read-only.");
            return;
        }
        if (assignment.EqualToken.TokenType != TokenType.Equal)
        {
            Error(assignment, "MSV004", "Compound assignments are not supported. Use an explicit expression.");
            return;
        }
        if (locals.TryGetValue(Key(variable), out var previous) && Merge(previous, value).Kind == ShapeKind.Unknown)
        {
            Error(assignment, "MSV005", "Assignments must preserve the shape of an existing local variable.", variable.Name);
            return;
        }
        locals[Key(variable)] = value;
    }

    private void If(ScriptIfStatement condition)
    {
        Infer(condition.Condition);
        var before = new Dictionary<string, ModelShape>(locals, StringComparer.Ordinal);
        Visit(condition.Then);
        var then = locals;
        locals = new Dictionary<string, ModelShape>(before, StringComparer.Ordinal);
        Visit(condition.Else);
        // A variable becomes definitely assigned only if both branches define it.
        locals = then.Where(p => locals.ContainsKey(p.Key)).ToDictionary(p => p.Key,
            p => Merge(p.Value, locals[p.Key]), StringComparer.Ordinal);
    }

    private void For(ScriptForStatement loop)
    {
        var iterator = Infer(loop.Iterator);
        if (iterator.Kind is not (ShapeKind.Array or ShapeKind.Null or ShapeKind.Unknown))
            Error(loop.Iterator!, "MSV005", "A for iterator must be a typed collection or range.");
        if (loop.NamedArguments.Count != 0)
            Error(loop, "MSV004", "Loop modifiers are not supported in this milestone.");
        if (loop.Variable is not ScriptVariable variable || SafeBuiltins.IsReserved(variable.Name) ||
            locals.ContainsKey(Key(variable)) ||
            variable.Scope == ScriptVariableScope.Global && root.Members.ContainsKey(variable.Name))
        {
            Error(loop, "MSV004", "The loop variable must be a local name that does not replace a model member or builtin.");
            return;
        }
        var before = new Dictionary<string, ModelShape>(locals, StringComparer.Ordinal);
        locals[Key(variable)] = iterator.Element ?? ModelShape.Unknown;
        var loopShape = new ModelShape(ShapeKind.Object);
        foreach (var name in new[] { "index", "index0", "length", "rindex", "rindex0" })
            loopShape.Members.Add(name, new(null, ModelShape.Number));
        foreach (var name in new[] { "first", "last", "even", "odd", "changed" })
            loopShape.Members.Add(name, new(null, ModelShape.Boolean));
        locals["for"] = loopShape;
        loops++;
        Visit(loop.Body);
        loops--;
        // A loop may execute zero times. Preserve only pre-existing locals, conservatively
        // merging their possible types after loop assignments. Loop names are lexical here.
        var after = locals;
        foreach (var (name, shape) in before)
            if (name != "for" && after.TryGetValue(name, out var assigned) && Merge(shape, assigned).Kind == ShapeKind.Unknown)
                Error(loop, "MSV005", "Loop assignments must preserve the shape of existing local variables.", name);
        locals = before.ToDictionary(p => p.Key,
            p => after.TryGetValue(p.Key, out var value) ? Merge(p.Value, value) : p.Value, StringComparer.Ordinal);
        if (before.TryGetValue(Key(variable), out var original)) locals[Key(variable)] = original;
        if (before.TryGetValue("for", out var outer)) locals["for"] = outer;
        var afterLoop = new Dictionary<string, ModelShape>(locals, StringComparer.Ordinal);
        locals = new Dictionary<string, ModelShape>(before, StringComparer.Ordinal);
        Visit(loop.Else);
        locals = afterLoop.ToDictionary(p => p.Key, p => Merge(p.Value, locals[p.Key]), StringComparer.Ordinal);
    }

    private ModelShape Infer(ScriptExpression? expression)
    {
        if (expression is null) return ModelShape.Unknown;
        cancellationToken.ThrowIfCancellationRequested();
        if (++depth > Limits.AstDepth)
        {
            Error(expression, "MSV006", "Template AST exceeds the depth limit.");
            depth--;
            return ModelShape.Unknown;
        }
        try
        {
            switch (expression)
            {
                case ScriptLiteral literal:
                    return literal.Value switch
                    {
                        null => ModelShape.Null, string or char => ModelShape.String,
                        bool => ModelShape.Boolean, _ => ModelShape.Number
                    };
                case ScriptVariable variable:
                    if (locals.TryGetValue(Key(variable), out var local)) return local;
                    if (variable.Scope == ScriptVariableScope.Global && root.Members.TryGetValue(variable.Name, out var rootMember)) return rootMember.Shape;
                    var suggestion = Suggest(variable.Name);
                    Error(variable, "MSV002", $"Unknown variable '{variable.Name}'.{suggestion}", variable.Name);
                    return ModelShape.Unknown;
                case ScriptMemberExpression member:
                    return Member(Infer(member.Target), member.Member!.Name, member);
                case ScriptIndexerExpression indexer:
                    var target = Infer(indexer.Target);
                    var index = Infer(indexer.Index);
                    if (target.Kind == ShapeKind.Object && indexer.Index is ScriptLiteral { Value: string key })
                        return Member(target, key, indexer);
                    if (target.Kind == ShapeKind.Array && index.Kind == ShapeKind.Number) return target.Element!;
                    if (target.Kind == ShapeKind.String && index.Kind == ShapeKind.Number) return ModelShape.String;
                    Error(indexer, "MSV003", "Indexer requires a numeric collection index or a constant declared member name.");
                    return ModelShape.Unknown;
                case ScriptNestedExpression nested: return Infer(nested.Expression);
                case ScriptConditionalExpression conditional:
                    Infer(conditional.Condition);
                    return Merge(Infer(conditional.ThenValue), Infer(conditional.ElseValue));
                case ScriptBinaryExpression binary:
                    var left = Infer(binary.Left);
                    var right = Infer(binary.Right);
                    var op = binary.Operator.ToString();
                    if (op is "RangeInclude" or "RangeExclude") return new ModelShape(ShapeKind.Array) { Element = ModelShape.Number };
                    if (op is "CompareEqual" or "CompareNotEqual" or "CompareLess" or "CompareLessOrEqual" or
                        "CompareGreater" or "CompareGreaterOrEqual" or "And" or "Or") return ModelShape.Boolean;
                    if (op is "EmptyCoalescing" or "NullCoalescing") return Merge(left, right);
                    if (op is "Add" or "Subtract" or "Multiply" or "Divide" or "DivideRound" or "Modulus")
                        return left.Kind == ShapeKind.String || right.Kind == ShapeKind.String ? ModelShape.String :
                            left.Kind == ShapeKind.Array ? left : ModelShape.Number;
                    Error(binary, "MSV004", "This binary operator is not supported.");
                    return ModelShape.Unknown;
                case ScriptUnaryExpression unary when unary.Operator.ToString() is "Not" or "Negate" or "Plus":
                    var operand = Infer(unary.Right);
                    return unary.Operator.ToString() == "Not" ? ModelShape.Boolean : operand;
                case ScriptFunctionCall call: return Call(call.Target, call.Arguments, null, call);
                case ScriptPipeCall pipe:
                    var input = Infer(pipe.From);
                    return ApplyPipe(pipe.To, input, pipe);
                case ScriptArrayInitializerExpression array:
                    ModelShape? element = null;
                    foreach (var value in array.Values)
                    {
                        var valueShape = Infer(value);
                        element = element is null ? valueShape : Merge(element, valueShape);
                    }
                    return new ModelShape(ShapeKind.Array) { Element = element ?? ModelShape.Unknown };
                default:
                    Error(expression, "MSV004", "This Scriban expression is not supported by MailStencil.");
                    return ModelShape.Unknown;
            }
        }
        finally { depth--; }
    }

    private ModelShape ApplyPipe(ScriptExpression? target, ModelShape input, ScriptNode node)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++depth > Limits.AstDepth)
        {
            Error(node, "MSV006", "Template AST exceeds the depth limit.");
            depth--;
            return ModelShape.Unknown;
        }
        try
        {
            if (target is ScriptPipeCall pipe)
                return ApplyPipe(pipe.To, ApplyPipe(pipe.From, input, pipe), pipe);
            return target is ScriptFunctionCall call
                ? Call(call.Target, call.Arguments, input, node)
                : Call(target, [], input, node);
        }
        finally { depth--; }
    }

    private ModelShape Call(ScriptExpression? target, IEnumerable<ScriptExpression> arguments, ModelShape? pipeInput, ScriptNode node)
    {
        var args = arguments.Select(Infer).ToList();
        if (pipeInput is not null) args.Insert(0, pipeInput);
        if (target is not ScriptMemberExpression { Target: ScriptVariableGlobal group, Member: { } function } ||
            !SafeBuiltins.Functions.TryGetValue($"{group.Name}.{function.Name}", out var signature))
        {
            Error(node, "MSV004", "Only explicitly allowed builtin functions can be called.");
            return ModelShape.Unknown;
        }
        if (args.Count < signature.MinArguments || args.Count > signature.MaxArguments)
            Error(node, "MSV005", "Invalid number of builtin function arguments.");
        return signature.Result;
    }

    private ModelShape Member(ModelShape target, string name, ScriptNode node)
    {
        if (target.Members.TryGetValue(name, out var member)) return member.Shape;
        Error(node, "MSV003", $"Unknown or invalid member '{name}' in this member chain.", name);
        return ModelShape.Unknown;
    }

    private static ModelShape Merge(ModelShape a, ModelShape b)
    {
        if (a == b) return a;
        if (a.Kind == ShapeKind.Null) return b;
        if (b.Kind == ShapeKind.Null) return a;
        if (a.Kind == b.Kind && a.Kind is ShapeKind.String or ShapeKind.Number or ShapeKind.Boolean) return a;
        if (a.Kind == ShapeKind.Array && b.Kind == ShapeKind.Array)
            return new ModelShape(ShapeKind.Array) { Element = Merge(a.Element!, b.Element!) };
        return ModelShape.Unknown;
    }

    private static string Key(ScriptVariable variable) => variable.Scope == ScriptVariableScope.Local ? "$" + variable.Name : variable.Name;

    private string Suggest(string name)
    {
        if (name.Length > 128) return string.Empty;
        var match = root.Members.Keys.Where(k => k.Length <= 128)
            .Select(k => (Name: k, Distance: Distance(name, k)))
            .Where(p => p.Distance <= 2).OrderBy(p => p.Distance).ThenBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault();
        return match.Name is null ? string.Empty : $" Did you mean '{match.Name}'?";
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = old;
            }
        }
        return row[b.Length];
    }

    private void Error(ScriptNode node, string code, string message, string? member = null) =>
        diagnostics.Add(new(component, TemplateDiagnosticSeverity.Error, code, message, Location(node.Span), member));

    internal static TemplateSourceSpan Location(SourceSpan span) => new(span.Start.Offset,
        Math.Max(0, span.End.Offset - span.Start.Offset + 1), span.Start.Line + 1, span.Start.Column + 1);
}
