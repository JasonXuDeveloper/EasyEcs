using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace EasyEcs.Generator;

/// <summary>
/// The user's own calls in a [ForEach] method, inlined before the vector loop is translated (Burst's idea: the method
/// is the kernel of one lane, whatever helpers it calls): a static helper, an extension method or C# 14 extension
/// member, an instance method or a property with a body of a struct (a component), a local function, whose source is in
/// the compilation. Each call becomes the callee's body with its parameters bound to the caller's values: a value
/// parameter to the argument itself (or a local holding it), a <c>ref</c> / <c>in</c> / <c>out</c> parameter to the
/// caller's variable, <c>this</c> to the receiver; its locals renamed, its names qualified. A value is an expression
/// where the body is one (<c>=&gt; e</c>, or <c>if</c> / <c>return</c> only, as <c>?:</c>), else a local the body assigns
/// before the statement that uses it; an early <c>return</c> leaves the rest of the body to the other branch, or to the
/// lanes a flag still holds. The rewritten body is bound speculatively in the method's own semantic model (no copy of the
/// compilation), and the vector loop is that body's: a helper costs what the same code written inline costs. A call the
/// inliner leaves (virtual, recursive, in another assembly, a loop or a lambda in its body) keeps its place, and the
/// reason the loop gives names it (<see cref="CallReason"/>). Callee bodies are read once per compilation
/// (<see cref="Callees"/>), refused from their syntax before any of them is bound.
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>How deep helpers may call helpers: past it, the call keeps its place.</summary>
    private const int MaxInlining = 8;

    /// <summary>
    /// The kind of the annotation on a call the inliner left in place: its data is "F" (fixable) or "I" followed by the
    /// reason the vector loop gives for it.
    /// </summary>
    private const string CallReason = "EasyEcs.CallReason";

    /// <summary>
    /// The method's body with its calls inlined, bound speculatively in <paramref name="model"/> (null: no call to
    /// inline, or the body could not be bound; the method is then translated as written).
    /// </summary>
    private static (BlockSyntax Body, SemanticModel Model)? InlineCalls(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model,
        IOperation body, System.Threading.CancellationToken ct)
    {
        var callees = Callees.Of(model.Compilation);
        callees.Offer(model, syntax);
        if (!body.DescendantsAndSelf().Any(o => callees.Candidate(o) != null)) return null;
        var used = new HashSet<string>(syntax.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText));
        var inliner = new Inliner(used, method, model.Compilation, ct);
        var rewriter = inliner.Method(model);
        var block = syntax.Body != null ? rewriter.Body(syntax.Body)
            : syntax.ExpressionBody != null ? Block(rewriter.Arrow(syntax.ExpressionBody.Expression, null)) : null;
        if (block == null || inliner.Count + inliner.Left == 0) return null;
        block = inliner.WithAccessors(Inliner.WithoutInlinedLocalFunctions(block));
        var replaced = syntax.WithExpressionBody(null).WithSemicolonToken(default).WithBody(block);
        int position = syntax.Body?.SpanStart ?? syntax.ExpressionBody!.SpanStart;
        if (!model.TryGetSpeculativeSemanticModelForMethodBody(position, replaced, out var speculative) || speculative == null) return null;
        return (replaced.Body!, speculative);
    }

    /// <summary>
    /// Why the vector loop cannot take a call of <paramref name="method"/> (the call <paramref name="op"/>): the inliner's
    /// reason for a call it left, else the callee's own (virtual, an interface's, a delegate, in another assembly, what
    /// its body holds).
    /// </summary>
    private (string Text, bool IsFixable) ReasonForCall(IOperation op, IMethodSymbol method)
    {
        if (op.Syntax.GetAnnotations(CallReason).FirstOrDefault()?.Data is { Length: > 1 } data) return (data.Substring(1), data[0] == 'F');
        // A key probe (TryFind / Find / Count of a keyed Lookup) is EasyEcs's own, not a call to inline: it walks the
        // index per row, which no gather replaces (an index lookup stays scalar, D131).
        if (Chases.IsKeyLookup(method))
            return ($"it probes an index ('{Text(op)}'): a key probe has no vector form; a vector loop reads other entities only at an EntityId", false);
        string call = $"it calls {(method.ContainingType.IsExtension ? method.ContainingType.ContainingType : method.ContainingType).Name}.{Callees.NameOf(method)}";
        if (_compilation != null && Callees.Of(_compilation).Refusal(method, default) is { } why) return ($"{call}, {why}", false);
        return (_lambda ? $"{call}: a lambda pass does not inline calls; a [ForEach] method does" : call, false);
    }

    /// <summary>What in a body keeps a vector loop from inlining it, from its syntax alone (null: nothing); Recorder.cs records it.</summary>
    internal static string? BodyRefusal(SyntaxNode body) => Callee.Refused(body);

    /// <summary>
    /// The semantic model of <paramref name="tree"/> in <paramref name="compilation"/>, one per tree (<see cref="Callees"/>):
    /// what it binds (a Combine, a constructor, a Deconstruct read by every method that uses them) is bound once per
    /// compilation, not once per method, as a new model from <c>Compilation.GetSemanticModel</c> would.
    /// </summary>
    internal static SemanticModel ModelOf(Compilation compilation, SyntaxTree tree) => Callees.Of(compilation).ModelOf(tree);

    /// <summary>A lambda pass's body is translated (it inlines no call).</summary>
    private bool _lambda;

    // ---------------- callees ----------------

    /// <summary>
    /// What the inliner knows of the members of one compilation, read once per member: which call it may inline
    /// (<see cref="Candidate"/>), the callee's body and why it refuses one (<see cref="Refusal"/>, from the syntax first).
    /// Kept per compilation (a ConditionalWeakTable on it), so an edit starts afresh and the analyzer's compilation and the
    /// generator's never share one.
    /// </summary>
    private sealed class Callees
    {
        private static readonly ConditionalWeakTable<Compilation, Callees> Table = new();
        private readonly Compilation _compilation;
        private readonly ConcurrentDictionary<IMethodSymbol, Callee> _callees = new(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<SyntaxTree, SemanticModel> _models = new();

        private Callees(Compilation compilation) => _compilation = compilation;

        public static Callees Of(Compilation compilation) => Table.GetValue(compilation, static c => new Callees(c));

        /// <summary>
        /// A method body of the compilation that recorded bodies of other assemblies are bound in (as static local
        /// functions in its place, speculatively: <see cref="Callee.Read"/>), the first one offered. Their names are all
        /// qualified, so where they are bound changes nothing.
        /// </summary>
        private Host? _host;

        private sealed record Host(SemanticModel Model, MethodDeclarationSyntax Method);

        /// <summary>Offers <paramref name="method"/> as the place recorded bodies are bound in (<see cref="_host"/>).</summary>
        public void Offer(SemanticModel model, MethodDeclarationSyntax method)
        {
            if (_host == null && (method.Body != null || method.ExpressionBody != null))
                System.Threading.Interlocked.CompareExchange(ref _host, new Host(model, method), null);
        }

        /// <summary>The name of <paramref name="method"/> as code writes it (a property's, for an accessor).</summary>
        public static string NameOf(IMethodSymbol method) => method.AssociatedSymbol is IPropertySymbol property ? property.Name : method.Name;

        public SemanticModel ModelOf(SyntaxTree tree) => _models.GetOrAdd(tree, t => _compilation.GetSemanticModel(t));

        /// <summary>
        /// A recorded body of another assembly bound as static local functions in the place of a method of this
        /// compilation (<see cref="_host"/>): the block and its speculative model, whose tree <see cref="ModelOf"/> then
        /// knows, so the local functions of its helpers are read like the user's own. Null: no place offered yet, or the
        /// body does not parse.
        /// </summary>
        public (BlockSyntax Block, SemanticModel Model)? Bind(Recorded.BodyFact fact)
        {
            if (_host is not { } host || Recorded.Parse(fact) is not { } block) return null;
            var replaced = host.Method.WithExpressionBody(null).WithSemicolonToken(default).WithBody(block);
            int position = host.Method.Body?.SpanStart ?? host.Method.ExpressionBody!.SpanStart;
            if (!host.Model.TryGetSpeculativeSemanticModelForMethodBody(position, replaced, out var speculative) || speculative == null) return null;
            _models[replaced.SyntaxTree] = speculative;
            return (replaced.Body!, speculative);
        }

        /// <summary>The definition a call or a property access <paramref name="op"/> reaches, when its source is the user's to inline.</summary>
        public IMethodSymbol? Candidate(IOperation op) => op switch
        {
            IInvocationOperation call => Candidate(call.TargetMethod),
            IPropertyReferenceOperation { Property: var p } access => Candidate(IsWritten(access) ? p.SetMethod : p.GetMethod),
            _ => null,
        };

        private static bool IsWritten(IOperation op) => op.Parent is ISimpleAssignmentOperation assignment && assignment.Target == op;

        /// <summary>
        /// The definition of <paramref name="method"/> when the inliner may take its calls: an ordinary method, a local
        /// function or an accessor with a body in this compilation, which the generators did not write, or whose body its
        /// assembly recorded (Recorded.cs), called directly (not virtual, not through an interface), static or of a
        /// struct. A recorded body's placeholder is no candidate: the vector loop reads the field it stands for.
        /// </summary>
        public IMethodSymbol? Candidate(IMethodSymbol? method)
        {
            if (method == null) return null;
            var definition = Definition(method);
            if (definition.MethodKind is not (MethodKind.Ordinary or MethodKind.LocalFunction or MethodKind.PropertyGet or MethodKind.PropertySet)) return null;
            if (definition.DeclaringSyntaxReferences.IsEmpty ? _host == null || Recorded.BodyOf(definition) is not { Reason: null }
                : Symbols.IsGenerated(definition) || Recorded.AccessOf(definition) != null)
                return null;
            if (SymbolicReason(definition) != null) return null;
            return Get(definition).Declaration != null ? definition : null;
        }

        /// <summary>The method a call names, as declared: a reduced extension method's static form, unconstructed.</summary>
        public static IMethodSymbol Definition(IMethodSymbol method) => (method.ReducedFrom ?? method).OriginalDefinition;

        /// <summary>
        /// Why the inliner leaves every call of <paramref name="method"/> (the text after the callee's name: "which is
        /// virtual"), or null when it may take one: what its symbol says, then what its body's syntax holds.
        /// </summary>
        public string? Refusal(IMethodSymbol method, System.Threading.CancellationToken ct)
        {
            var definition = Definition(method);
            if (SymbolicReason(definition) is { } symbolic) return symbolic;
            if (definition.MethodKind is not (MethodKind.Ordinary or MethodKind.LocalFunction or MethodKind.PropertyGet or MethodKind.PropertySet)) return null;
            if (definition.DeclaringSyntaxReferences.IsEmpty)
                return Recorded.BodyOf(definition) switch
                {
                    null => "which is defined in another assembly",
                    { Reason: { } reason } => reason,
                    _ => _host == null ? null : Get(definition, ct).Refusal,
                };
            if (Symbols.IsGenerated(definition)) return null;
            return Get(definition, ct).Refusal;
        }

        private static string? SymbolicReason(IMethodSymbol m)
        {
            if (m.MethodKind == MethodKind.DelegateInvoke) return "a delegate, whose target the vector loop cannot see";
            if (m.ContainingType.TypeKind == TypeKind.Interface && !m.IsStatic) return "an interface's member, whose implementation the vector loop cannot see";
            if (m.IsAbstract || m.IsVirtual || m.IsOverride && !m.ContainingType.IsValueType && !m.IsSealed && !m.ContainingType.IsSealed)
                return "which is virtual: the vector loop cannot see which override runs";
            if (!m.IsStatic && m.MethodKind != MethodKind.LocalFunction && m.ContainingType.TypeKind == TypeKind.Class)
                return $"an instance method of class {m.ContainingType.Name}: the vector loop takes the values of structs only";
            return null;
        }

        public Callee Get(IMethodSymbol definition, System.Threading.CancellationToken ct = default) =>
            _callees.GetOrAdd(definition, d => Callee.Read(d, this, ct));
    }

    /// <summary>
    /// A callee's body (one expression or a block) and model, or the reason the inliner refuses it from its syntax
    /// alone; its effects (<see cref="Facts"/>) are bound on first use.
    /// </summary>
    private sealed class Callee
    {
        public IMethodSymbol Method { get; private set; } = null!;
        /// <summary>The declaration: a method, a local function, an accessor or a property's arrow clause (null: refused).</summary>
        public SyntaxNode? Declaration { get; private set; }
        public BlockSyntax? Block { get; private set; }
        public ExpressionSyntax? Expression { get; private set; }
        public SemanticModel Model { get; private set; } = null!;
        public string? Refusal { get; private set; }

        /// <summary>
        /// The parameters the body names, in order: the member's own, or a recorded body's (its local function's), after
        /// its <see cref="Receiver"/>.
        /// </summary>
        public System.Collections.Immutable.ImmutableArray<IParameterSymbol> Parameters { get; private set; }

        /// <summary>The type parameters the body names: the member's own, or a recorded body's local function's.</summary>
        public System.Collections.Immutable.ImmutableArray<ITypeParameterSymbol> TypeParameters { get; private set; }

        /// <summary>A recorded body of an instance member: the parameter its receiver is bound to (named <c>this</c>), or null.</summary>
        public IParameterSymbol? Receiver { get; private set; }

        /// <summary>Every parameter the body names, its receiver's included.</summary>
        public IEnumerable<IParameterSymbol> AllParameters => Receiver == null ? Parameters : Parameters.Prepend(Receiver);

        private Callees _callees = null!;
        private Effects? _effects;

        // The callees whose Facts this thread is reading, and those of them a read reached again (recursive). Per thread:
        // the analyzer reads one compilation's callees on several threads at once.
        [System.ThreadStatic] private static HashSet<Callee>? _reading;
        [System.ThreadStatic] private static HashSet<Callee>? _cycled;

        public static Callee Read(IMethodSymbol method, Callees callees, System.Threading.CancellationToken ct)
        {
            var callee = new Callee { Method = method, _callees = callees, Parameters = method.Parameters, TypeParameters = method.TypeParameters };
            if (method.DeclaringSyntaxReferences.IsEmpty) return callee.ReadRecorded(ct);
            var declaration = (method.PartialImplementationPart ?? method).DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct);
            (callee.Block, callee.Expression) = declaration switch
            {
                MethodDeclarationSyntax m => (m.Body, m.ExpressionBody?.Expression),
                LocalFunctionStatementSyntax f => (f.Body, f.ExpressionBody?.Expression),
                AccessorDeclarationSyntax a => (a.Body, a.ExpressionBody?.Expression),
                ArrowExpressionClauseSyntax arrow => (null, arrow.Expression),
                _ => ((BlockSyntax?)null, (ExpressionSyntax?)null),
            };
            if (callee.Block == null && callee.Expression == null)
            {
                callee.Refusal = "which has no body";
                return callee;
            }

            callee.Refusal = Refused((SyntaxNode?)callee.Block ?? callee.Expression!);
            if (callee.Refusal != null) return callee;
            callee.Declaration = declaration;
            callee.Model = callees.ModelOf(declaration!.SyntaxTree);
            return callee;
        }

        /// <summary>
        /// A body its assembly recorded (Recorded.cs), bound in the place the compilation offered (<see cref="Callees.Bind"/>):
        /// its first local function is the member, whose receiver, for an instance member, is its first parameter.
        /// </summary>
        private Callee ReadRecorded(System.Threading.CancellationToken ct)
        {
            if (Recorded.BodyOf(Method) is not { } fact) return Refuse("which is defined in another assembly");
            if (fact.Reason != null) return Refuse(fact.Reason);
            if (_callees.Bind(fact) is not var (block, model)
                || block.Statements.OfType<LocalFunctionStatementSyntax>().FirstOrDefault(f => f.Identifier.ValueText == Recorded.Body) is not { } function
                || model.GetDeclaredSymbol(function, ct) is not IMethodSymbol bound)
                return Refuse("whose recorded body the generator could not read");
            // What another assembly hides that the record still names (a newer compiler's rule): nothing the generated code could compile.
            if (model.GetOperation(block, ct) is not { } whole || whole.DescendantsAndSelf().Any(o => o is IInvalidOperation))
                return Refuse("whose recorded body does not bind in this assembly");
            bool instance = !Method.IsStatic && !Method.ContainingType.IsExtension;
            if (instance && bound.Parameters.Length == 0 || bound.Parameters.Length != Method.Parameters.Length + (instance ? 1 : 0)
                || bound.TypeParameters.Length != Method.TypeParameters.Length)
                return Refuse("whose recorded body does not match its declaration");
            (Receiver, Parameters, TypeParameters) = (instance ? bound.Parameters[0] : null, instance ? bound.Parameters.RemoveAt(0) : bound.Parameters, bound.TypeParameters);
            (Block, Expression) = (function.Body, function.ExpressionBody?.Expression);
            if ((Refusal = Refused((SyntaxNode?)Block ?? Expression!)) != null) return this;
            (Declaration, Model) = (function, model);
            return this;
        }

        private Callee Refuse(string reason)
        {
            Refusal = reason;
            return this;
        }

        /// <summary>What in a body keeps the vector loop from taking it, from the syntax alone (null: nothing).</summary>
        internal static string? Refused(SyntaxNode body)
        {
            foreach (var node in body.DescendantNodesAndSelf())
            {
                string? why = node switch
                {
                    AnonymousFunctionExpressionSyntax => "which declares a lambda",
                    LocalFunctionStatementSyntax => "which declares a local function",
                    ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax => "which has a loop",
                    TryStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax => "which throws or catches",
                    GotoStatementSyntax { CaseOrDefaultKeyword.RawKind: not 0 } => "which jumps from one switch section to another (goto case)",
                    BreakStatementSyntax when node.Parent is SwitchSectionSyntax => null, // a section's end (Kernel.Switches.cs)
                    GotoStatementSyntax or LabeledStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax => "which jumps (goto, break, continue)",
                    UsingStatementSyntax or LockStatementSyntax or FixedStatementSyntax or UnsafeStatementSyntax or CheckedStatementSyntax => "which has a using, lock, fixed, unsafe or checked block",
                    YieldStatementSyntax => "which is an iterator",
                    AwaitExpressionSyntax => "which awaits",
                    StackAllocArrayCreationExpressionSyntax => "which allocates on the stack",
                    _ => null,
                };
                if (why != null) return why;
            }

            return null;
        }

        /// <summary>
        /// What the body writes, bound once: the parameters it writes (assigned, incremented, passed by ref or out, or
        /// a struct parameter's method called), whether it writes <c>this</c>, and whether it is pure (it writes nothing
        /// but its own locals and value parameters, and calls nothing but math and pure helpers), so a value it returns
        /// may be computed before the statement that uses it.
        /// </summary>
        public Effects Facts(System.Threading.CancellationToken ct)
        {
            if (_effects != null) return _effects;
            var reading = _reading ??= new HashSet<Callee>();
            if (!reading.Add(this))
            {
                (_cycled ??= new HashSet<Callee>()).Add(this); // a cycle: not pure, and not inlined either (Enter refuses it)
                return new Effects(new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default), true, false, null, true);
            }
            try
            {
                var written = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
                bool writesThis = false, pure = true;
                string? opaque = null;
                var root = Model.GetOperation((SyntaxNode?)Block ?? Expression!, ct);
                foreach (var op in root?.DescendantsAndSelf() ?? [])
                {
                    IOperation? target = op switch
                    {
                        IAssignmentOperation a => a.Target,
                        IIncrementOrDecrementOperation i => i.Target,
                        IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } arg => arg.Value,
                        // A struct's own method may write it; an extension member only through a 'ref' receiver (the generated
                        // ones, unresolved in the generator, take the entity by value: the analyzer must read them alike).
                        IInvocationOperation { Instance: { } instance, TargetMethod: { IsReadOnly: false, IsStatic: false } called } when instance.Type?.IsValueType == true
                            && (!called.ContainingType.IsExtension || called.ContainingType.ExtensionParameter?.RefKind == RefKind.Ref) => instance,
                        _ => null,
                    };
                    if (target != null)
                    {
                        switch (Root(target))
                        {
                            case IParameterReferenceOperation { Parameter: var p }:
                                written.Add(p);
                                if (p.RefKind != RefKind.None) pure = false; // a value parameter is the callee's own copy
                                break;
                            case IInstanceReferenceOperation:
                                writesThis = true;
                                pure = false;
                                break;
                            case ILocalReferenceOperation:
                                break;
                            default:
                                pure = false;
                                break;
                        }
                    }

                    if (opaque == null && op is IInvocationOperation other && !IsMath(other.TargetMethod) && !KnownVector(other.TargetMethod.ContainingType)
                        && _callees.Candidate(other.TargetMethod) == null && !Symbols.IsGenerated(other.TargetMethod) && Recorded.AccessOf(other.TargetMethod) == null)
                        opaque = $"{other.TargetMethod.ContainingType.Name}.{other.TargetMethod.Name}";

                    switch (op)
                    {
                        case IInvalidOperation:
                        case IInvocationOperation call when !IsMath(call.TargetMethod) && !KnownVector(call.TargetMethod.ContainingType) && !Pure(call.TargetMethod, ct)
                            && Recorded.AccessOf(call.TargetMethod) == null: // a placeholder's field: a write of it passes its struct by ref
                        case IPropertyReferenceOperation access when Symbols.IsGenerated(access.Property)
                            || _callees.Candidate(access.Property.GetMethod) is { } getter && !_callees.Get(getter, ct).Facts(ct).Pure:
                        case IObjectCreationOperation { Type.IsReferenceType: true }:
                            pure = false;
                            break;
                    }
                }

                bool recursive = _cycled?.Remove(this) == true;
                return _effects = new Effects(written, writesThis, pure && !recursive, opaque, recursive);
            }
            finally
            {
                reading.Remove(this);
            }
        }

        private bool Pure(IMethodSymbol method, System.Threading.CancellationToken ct) =>
            _callees.Candidate(method) is { } definition && _callees.Get(definition, ct).Facts(ct).Pure;
    }

    /// <param name="Written">The parameters the body writes.</param>
    /// <param name="WritesThis">It writes the struct it is a member of.</param>
    /// <param name="Pure">It changes nothing its caller sees (<see cref="Callee.Facts"/>).</param>
    /// <param name="Opaque">The first method it calls that no loop inlines (not math, not a helper of this compilation, not generated), or null.</param>
    /// <param name="Recursive">It reaches itself through the helpers it calls.</param>
    private sealed record Effects(HashSet<IParameterSymbol> Written, bool WritesThis, bool Pure, string? Opaque, bool Recursive);

    /// <summary>The variable a write reaches through fields: <c>a.b.c</c> is <c>a</c>.</summary>
    private static IOperation Root(IOperation op)
    {
        while (op is IFieldReferenceOperation or IPropertyReferenceOperation && ((IMemberReferenceOperation)op).Instance is { Type.IsValueType: true } instance)
            op = instance;
        return op;
    }

    /// <summary>A call the inliner leaves in place, with the reason the vector loop gives for it.</summary>
    private sealed class NotInlined(string reason, bool fixable = false) : System.Exception(reason)
    {
        public string Reason { get; } = reason;
        public bool Fixable { get; } = fixable;
    }

    // ---------------- the rewrite ----------------

    /// <summary>
    /// Rewrites a [ForEach] method's body: its calls inlined (<see cref="Kernel"/> remarks of Kernel.Inlining.cs), and,
    /// for a method taking views (Kernel.Views.cs), the views' fields as parameters and their members inlined.
    /// </summary>
    private sealed class Inliner(HashSet<string> used, IMethodSymbol root, Compilation compilation, System.Threading.CancellationToken ct)
    {
        /// <summary>Each view field, as the name of its parameter.</summary>
        public Dictionary<IFieldSymbol, string> Fields { get; } = new(SymbolEqualityComparer.Default);

        /// <summary>Why the views cannot be desugared (the first construct met), or null.</summary>
        public string? Reason { get; private set; }

        /// <summary>How many calls were inlined.</summary>
        public int Count { get; set; }

        /// <summary>How many calls were left in place with a reason of their own (the rewrite carries it to the loop).</summary>
        public int Left { get; set; }

        /// <summary>
        /// Inside an attempt to inline a value as one expression: a call in it that cannot be inlined so fails the attempt
        /// (the form with statements before it is tried next) instead of keeping its place.
        /// </summary>
        public int Folding { get; set; }

        public Callees Callees { get; } = Callees.Of(compilation);

        public IMethodSymbol Root => root;

        public System.Threading.CancellationToken Token => ct;

        /// <summary>The members being inlined, outermost first: a call of one of them is recursive.</summary>
        public List<IMethodSymbol> Stack { get; } = [];

        /// <summary>A name no identifier of the method or of a body inlined into it uses yet.</summary>
        public string Fresh(string stem)
        {
            string name = stem;
            while (!used.Add(name)) name = "_" + name;
            return name;
        }

        /// <summary>Names the inlined bodies use become taken (a local of the method may not shadow one of theirs).</summary>
        public void Reserve(SyntaxNode body)
        {
            foreach (var token in body.DescendantTokens())
                if (token.IsKind(SyntaxKind.IdentifierToken)) used.Add(token.ValueText);
        }

        /// <summary>The pass over the [ForEach] method's own body.</summary>
        public Rewriter Method(SemanticModel model) => new(this, new Scope(model, null, new(SymbolEqualityComparer.Default)));

        public void Fail(string reason)
        {
            if (Folding > 0) throw new NotInlined(reason);
            Reason ??= reason;
        }

        /// <summary>The placeholders the inlined recorded bodies use, by their field and form: the name and declaration each has in the method.</summary>
        private readonly Dictionary<string, (string Name, LocalFunctionStatementSyntax Declaration)> _accessors = new(System.StringComparer.Ordinal);

        /// <summary>
        /// The name a recorded body's placeholder <paramref name="placeholder"/> has in the method: one per field and form
        /// (read through <c>in</c>, written through <c>ref</c>, static), whatever body named it, named after the field.
        /// </summary>
        public string Accessor(IMethodSymbol placeholder, Recorded.Access access)
        {
            string form = placeholder.Parameters.Length == 0 ? "static" : placeholder.Parameters[0].RefKind.ToString();
            string key = $"{access.Assembly}|{access.Type}|{access.Name}|{form}";
            if (_accessors.TryGetValue(key, out var known)) return known.Name;
            if (placeholder.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is not LocalFunctionStatementSyntax declaration)
                throw new NotInlined($"whose recorded body names field {access.Name}, which the generator could not read");
            string type = access.Type.Substring(access.Type.LastIndexOfAny(['.', '+']) + 1);
            string name = Fresh($"__{type}_{access.Name.Replace('<', '_').Replace('>', '_')}{(form == "In" ? "_in" : "")}");
            _accessors[key] = (name, declaration.WithIdentifier(Identifier(name)));
            return name;
        }

        /// <summary>The method's body with the placeholders its inlined calls use declared at its end (Kernel reads them as fields).</summary>
        public BlockSyntax WithAccessors(BlockSyntax block) =>
            _accessors.Count == 0 ? block : block.AddStatements(_accessors.Values.OrderBy(a => a.Name, System.StringComparer.Ordinal).Select(a => (StatementSyntax)a.Declaration).ToArray());

        /// <summary>The body without the local functions every call of which was inlined (the vector loop takes none).</summary>
        public static BlockSyntax WithoutInlinedLocalFunctions(BlockSyntax block)
        {
            var functions = block.DescendantNodes().OfType<LocalFunctionStatementSyntax>().ToArray();
            var unused = functions.Where(f => !block.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Any(n => n.Identifier.ValueText == f.Identifier.ValueText && !n.Ancestors().Contains(f))).ToArray();
            return unused.Length == 0 ? block : block.RemoveNodes(unused, SyntaxRemoveOptions.KeepNoTrivia)!;
        }
    }

    /// <summary>
    /// Where a body comes from: its tree's model, the member it belongs to (null for the [ForEach] method), what its
    /// parameters, locals and type parameters become (<paramref name="Names"/>), and what <c>this</c> is: the receiver's
    /// expression (<see cref="This"/>), or the view itself in a view member (<see cref="ViewThis"/>).
    /// </summary>
    private sealed record Scope(SemanticModel Model, IMethodSymbol? Member, Dictionary<ISymbol, ExpressionSyntax> Names)
    {
        public ExpressionSyntax? This { get; init; }
        public bool ViewThis { get; init; }
        /// <summary>The receiver of an instance member is a variable the member may write (else a copy is written).</summary>
        public bool ThisWritable { get; init; } = true;
    }

    /// <summary>One body's rewrite. Every node it binds is an original node of <see cref="Scope.Model"/>'s tree.</summary>
    private sealed partial class Rewriter(Inliner owner, Scope scope) : CSharpSyntaxRewriter
    {
        /// <summary>The statements the calls of the statement being rewritten need before it (null: inside an expression only).</summary>
        private List<StatementSyntax>? _prefix;

        /// <summary>The original statement being rewritten.</summary>
        private StatementSyntax? _statement;

        private bool Inlined => scope.Member != null;

        private ISymbol? Bound(SyntaxNode node) => scope.Model.GetSymbolInfo(node, owner.Token).Symbol;

        // ---------------- statements ----------------

        public BlockSyntax Body(BlockSyntax block) => (BlockSyntax)VisitBlock(block)!;

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var list = new List<StatementSyntax>();
            foreach (var statement in node.Statements) list.AddRange(Statements(statement));
            return node.WithStatements(List(list));
        }

        /// <summary>A statement rewritten, after the statements its calls need (a callee's locals, a hoisted value).</summary>
        public List<StatementSyntax> Statements(StatementSyntax statement) => Statement(statement, () => statement switch
        {
            ExpressionStatementSyntax e => ExpressionStatement(e.Expression) is { } rewritten ? e.WithExpression(rewritten) : null,
            IfStatementSyntax f => If(f),
            SwitchStatementSyntax s => Switch(s), // Kernel.Switches.cs
            BreakStatementSyntax when statement.Parent is SwitchSectionSyntax => statement,
            BlockSyntax b => Body(b),
            LocalDeclarationStatementSyntax or ReturnStatementSyntax or EmptyStatementSyntax => (StatementSyntax?)Visit(statement),
            LocalFunctionStatementSyntax => statement, // inlined at its calls; dropped when all of them are
            _ => Leave(statement), // the vector loop gives its reason; nothing in it is inlined
        });

        /// <summary>
        /// An arrow body (<c>=&gt; e</c>) as statements: <c>e;</c> (a value dropped: an expression that is no statement
        /// is dropped whole when it changes nothing), or <c>result = e;</c>.
        /// </summary>
        public List<StatementSyntax> Arrow(ExpressionSyntax expression, string? result) => Statement(null, () => result != null
            ? Assign(result, (ExpressionSyntax)Visit(expression)!)
            : ExpressionStatement(expression) is { } rewritten ? SyntaxFactory.ExpressionStatement(rewritten) : null);

        private List<StatementSyntax> Statement(StatementSyntax? statement, System.Func<StatementSyntax?> rewrite)
        {
            owner.Token.ThrowIfCancellationRequested();
            var (prefix, current) = (_prefix, _statement);
            _prefix = [];
            _statement = statement;
            try
            {
                var rewritten = rewrite();
                var list = _prefix;
                if (rewritten != null) list.Add(rewritten);
                return list;
            }
            finally
            {
                (_prefix, _statement) = (prefix, current);
            }
        }

        /// <summary>A statement the inliner does not take apart: as written (in a member being inlined, a reason).</summary>
        private StatementSyntax Leave(StatementSyntax statement)
        {
            if (Inlined) throw new NotInlined($"its body has '{statement.GetFirstToken().ValueText}' statements");
            return statement;
        }

        private StatementSyntax Embedded(StatementSyntax statement)
        {
            var list = Statements(statement);
            return list.Count == 1 ? list[0] : Block(list);
        }

        private StatementSyntax If(IfStatementSyntax node)
        {
            var condition = (ExpressionSyntax)Visit(node.Condition)!;
            var then = Embedded(node.Statement);
            var @else = node.Else is { } e ? e.WithStatement(Embedded(e.Statement)) : null;
            return node.WithCondition(condition).WithStatement(then).WithElse(@else);
        }

        /// <summary>
        /// The expression of a statement: a call whose value is dropped inlined as a block (the block goes before, into the
        /// prefix, and nothing is left), a setter as one, anything else rewritten. Null: nothing left to evaluate.
        /// </summary>
        private ExpressionSyntax? ExpressionStatement(ExpressionSyntax expression)
        {
            StatementSyntax? inlined = null;
            if (expression is InvocationExpressionSyntax call && Site(call) is { } site)
            {
                inlined = InlineStatement(call, site, call.ArgumentList.Arguments);
                if (inlined == null) return (ExpressionSyntax)base.VisitInvocationExpression(call)!;
            }
            else if (expression is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Left: var left } assignment
                && Setter(left) is { } setter)
            {
                inlined = InlineStatement(assignment, setter, SingletonSeparatedList(Argument(assignment.Right)));
                if (inlined == null) return (ExpressionSyntax)base.VisitAssignmentExpression(assignment)!;
            }

            if (inlined == null) return Visit(expression) as ExpressionSyntax;
            if (inlined is ExpressionStatementSyntax kept) return kept.Expression; // the call left in place
            _prefix!.Add(inlined);
            return null;
        }

        // ---------------- sites ----------------

        /// <summary>
        /// A member the inliner takes, as one place calls it: its definition (<paramref name="Method"/>), the call's own
        /// symbol (constructed: its type arguments), the receiver as written (null: none, or <c>this</c>), and whether the
        /// receiver is the first argument (a reduced extension method) or a view.
        /// </summary>
        private sealed record CallSite(IMethodSymbol Method, IMethodSymbol Called, ExpressionSyntax? Receiver, bool ReceiverIsArgument, bool View);

        /// <summary>The site of a call the inliner may take, or null (a call it leaves as it is: math, the generated members, another assembly's).</summary>
        private CallSite? Site(InvocationExpressionSyntax call)
        {
            if (Bound(call) is not IMethodSymbol called) return null;
            ExpressionSyntax? receiver = call.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Expression,
                _ => null,
            };
            if (Views.IsView(called.ContainingType) && !called.IsStatic) // a view member: on the view only (Kernel.Views.cs)
                return IsView(receiver) ? new CallSite(called.OriginalDefinition, called, null, false, true) : null;
            if (owner.Callees.Candidate(called) is not { } definition) return null;
            if (called.ReducedFrom != null) return new CallSite(definition, called.GetConstructedReducedFrom() ?? called, receiver, true, false);
            if (called.IsStatic && !called.ContainingType.IsExtension || called.MethodKind == MethodKind.LocalFunction) receiver = null;
            return new CallSite(definition, called, receiver, false, false);
        }

        /// <summary>The setter a property write reaches, when the inliner takes it.</summary>
        private CallSite? Setter(ExpressionSyntax target)
        {
            if (Bound(target) is not IPropertySymbol { SetMethod: { } set } property || owner.Callees.Candidate(set) is not { } definition) return null;
            if (Views.IsView(property.ContainingType)) return null;
            var receiver = target is MemberAccessExpressionSyntax access ? access.Expression : null;
            return new CallSite(definition, set, property.IsStatic && !property.ContainingType.IsExtension ? null : receiver, false, false);
        }

        /// <summary>The getter a property read reaches, when the inliner takes it (not a write's target, not passed by ref).</summary>
        private CallSite? Getter(ExpressionSyntax node)
        {
            if (node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node
                || node.Parent is PostfixUnaryExpressionSyntax || node.Parent is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression }
                || node.Parent is ArgumentSyntax argument && !argument.RefKindKeyword.IsKind(SyntaxKind.None))
                return null;
            if (Bound(node) is not IPropertySymbol { GetMethod: { } get } property) return null;
            var receiver = node is MemberAccessExpressionSyntax access ? access.Expression : null;
            if (Views.IsView(property.ContainingType) && !property.IsStatic)
                return IsView(receiver) ? new CallSite(get.OriginalDefinition, get, null, false, true) : null;
            if (owner.Callees.Candidate(get) is not { } definition) return null;
            return new CallSite(definition, get, property.IsStatic && !property.ContainingType.IsExtension ? null : receiver, false, false);
        }

        /// <summary>The receiver is the view: <c>this</c> (or none) inside a view member, a view parameter in the method.</summary>
        private bool IsView(ExpressionSyntax? receiver)
        {
            if (receiver == null || receiver is ThisExpressionSyntax) return scope.ViewThis;
            return !Inlined && Bound(receiver) is IParameterSymbol p && Views.IsView(p.Type);
        }

        /// <summary>The call, left in place with the reason the vector loop gives for it.</summary>
        private ExpressionSyntax Left(ExpressionSyntax rewritten, NotInlined? why = null)
        {
            if (why == null) return rewritten;
            owner.Left++;
            return rewritten.WithAdditionalAnnotations(new SyntaxAnnotation(CallReason, (why.Fixable ? "F" : "I") + why.Reason));
        }

        private string Callee(CallSite site) =>
            $"it calls {(site.Method.ContainingType.IsExtension ? site.Method.ContainingType.ContainingType : site.Method.ContainingType).Name}.{Callees.NameOf(site.Method)}";

        // ---------------- expressions ----------------

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (Site(node) is not { } site) return base.VisitInvocationExpression(node);
            return InlineValue(node, site, node.ArgumentList.Arguments, () => (ExpressionSyntax)base.VisitInvocationExpression(node)!);
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (IsView(node.Expression) && Bound(node) is IFieldSymbol field)
            {
                if (owner.Fields.TryGetValue(field, out var name)) return IdentifierName(name).WithTriviaFrom(node);
                owner.Fail($"it reaches field '{field.Name}' of a view the vector loop cannot follow");
                return node;
            }
            if (Getter(node) is { } site) return InlineValue(node, site, default, () => (ExpressionSyntax)base.VisitMemberAccessExpression(node)!);
            return base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
        {
            if (scope.ViewThis) owner.Fail($"view member {scope.Member!.Name} uses the view as a value ('this')");
            else if (scope.This != null) return Parenthesize(scope.This).WithTriviaFrom(node);
            return node;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) => Name(node) ?? base.VisitIdentifierName(node);

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node) => Name(node) ?? base.VisitGenericName(node);

        /// <summary>
        /// A simple name. In the method, a view parameter used other than for a field, property or method fails. In an
        /// inlined body: a parameter, local or type parameter of the member, a member of <c>this</c>, a type or static
        /// member (qualified: the member's file may import other namespaces), a property with a body (inlined). Null: left
        /// as it is.
        /// </summary>
        private SyntaxNode? Name(SimpleNameSyntax node)
        {
            // The body moves into the generated job, outside the callee's type and file: what only they reach cannot go.
            if (Inlined && Hidden(node) is { } hidden)
                throw new NotInlined($"whose body names {LambdaPass.Describe(hidden)}: make it internal", true);
            if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node || node.Parent is QualifiedNameSyntax qualified && qualified.Right == node
                || node.Parent is AliasQualifiedNameSyntax // global::Game, as a recorded body writes every name
                || NamesSubpattern(node))
                return null; // the member of something: its receiver decides (a property pattern's, the value it tests)
            var symbol = Bound(node);
            if (symbol == null) return null;
            if (!Inlined)
            {
                if (symbol is IParameterSymbol p && Views.IsView(p.Type))
                    owner.Fail(node.Parent is MemberAccessExpressionSyntax
                        ? $"it reaches a member of view '{p.Name}' the vector loop cannot follow"
                        : $"it uses view '{p.Name}' as a value");
                else if (symbol is IPropertySymbol && Getter(node) is { } site)
                    return InlineValue(node, site, default, () => node);
                return null;
            }

            if (scope.Names.TryGetValue(symbol.OriginalDefinition, out var replaced) || scope.Names.TryGetValue(symbol, out replaced))
                return replaced.WithTriviaFrom(node);
            // A recorded body's placeholder of a field its assembly hides: declared in the method under a name of its own.
            if (symbol is IMethodSymbol placeholder && Recorded.AccessOf(placeholder) is { } stood)
                return IdentifierName(owner.Accessor(placeholder, stood)).WithTriviaFrom(node);
            switch (symbol)
            {
                case ITypeParameterSymbol:
                    break;
                case ITypeSymbol named:
                    return ParseTypeName(named.ToDisplayString(Symbols.Fqn)).WithTriviaFrom(node);
                case INamespaceSymbol ns:
                    return ParseName(ns.ToDisplayString(Symbols.Fqn)).WithTriviaFrom(node);
                case IMethodSymbol { MethodKind: MethodKind.LocalFunction }:
                    return null;
                case IParameterSymbol or ILocalSymbol when Of(symbol, owner.Root):
                    return null; // the method's own, captured by a local function inlined into it
                case IPropertySymbol { IsStatic: false } when Getter(node) is { } site:
                    return InlineValue(node, site, default, () => node);
                case { IsStatic: true } or IFieldSymbol { IsConst: true }:
                    if (symbol is IPropertySymbol && Getter(node) is { } staticGetter) return InlineValue(node, staticGetter, default, () => node);
                    var name = node is GenericNameSyntax generic ? (SimpleNameSyntax)base.VisitGenericName(generic)! : node.WithoutTrivia();
                    var type = symbol.ContainingType.IsExtension ? symbol.ContainingType.ContainingType : symbol.ContainingType;
                    return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, ParseExpression(type.ToDisplayString(Symbols.Fqn)), name).WithTriviaFrom(node);
                case IFieldSymbol or IPropertySymbol or IMethodSymbol when scope.ViewThis:
                    if (symbol is IFieldSymbol field && owner.Fields.TryGetValue(field, out var parameter)) return IdentifierName(parameter).WithTriviaFrom(node);
                    break;
                case IFieldSymbol or IPropertySymbol or IMethodSymbol when scope.This != null:
                    return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, Parenthesize(scope.This), node.WithoutTrivia()).WithTriviaFrom(node);
            }

            throw new NotInlined($"its body uses '{node.Identifier.ValueText}', which the vector loop cannot follow");
        }

        /// <summary>
        /// The private, protected or file-local symbol (or containing type) a name of an inlined body reaches, which the
        /// generated job cannot name; null for what stays in the body (locals, parameters, constants folded) or is inlined
        /// itself (a helper's own helpers).
        /// </summary>
        private ISymbol? Hidden(SimpleNameSyntax node) => Bound(node) switch
        {
            null or ILocalSymbol or IParameterSymbol or ITypeParameterSymbol or INamespaceSymbol or IRangeVariableSymbol or IDiscardSymbol => null,
            IMethodSymbol { MethodKind: MethodKind.LocalFunction } => null,
            IFieldSymbol { IsConst: true } => null,
            IMethodSymbol method when owner.Callees.Candidate(method) != null => null,
            IPropertySymbol property when owner.Callees.Candidate(property.GetMethod) != null && owner.Callees.Candidate(property.SetMethod ?? property.GetMethod) != null => null,
            var symbol => LambdaPass.Blocking(symbol),
        };

        private static bool Of(ISymbol symbol, IMethodSymbol method)
        {
            for (var container = symbol.ContainingSymbol; container != null; container = container.ContainingSymbol)
                if (SymbolEqualityComparer.Default.Equals(container, method)) return true;
            return false;
        }

        public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node)
        {
            if (!Inlined || scope.Model.GetDeclaredSymbol(node, owner.Token) is not ILocalSymbol local) return base.VisitVariableDeclarator(node);
            string name = owner.Fresh($"__{local.Name}");
            scope.Names[local] = IdentifierName(name);
            return ((VariableDeclaratorSyntax)base.VisitVariableDeclarator(node)!).WithIdentifier(Identifier(name));
        }

        public override SyntaxNode? VisitSingleVariableDesignation(SingleVariableDesignationSyntax node)
        {
            if (!Inlined || scope.Model.GetDeclaredSymbol(node, owner.Token) is not ILocalSymbol local) return base.VisitSingleVariableDesignation(node);
            string name = owner.Fresh($"__{local.Name}");
            scope.Names[local] = IdentifierName(name);
            return node.WithIdentifier(Identifier(name));
        }

        // ---------------- inlining ----------------

        /// <summary>
        /// A call whose value is dropped: the callee's body in a block (its parameters bound first), or null with the call
        /// left in place (annotated). A view member that cannot be inlined fails the whole method.
        /// </summary>
        private StatementSyntax? InlineStatement(ExpressionSyntax node, CallSite site, SeparatedSyntaxList<ArgumentSyntax> arguments)
        {
            int mark = _prefix!.Count;
            try
            {
                return Enter(site, () =>
                {
                    if (site.Method.ReturnsVoid) return Block(InlineBody(site, arguments, null));
                    // A value dropped: the body still runs, into a local nothing reads.
                    string dropped = owner.Fresh($"__{Callees.NameOf(site.Method)}");
                    var statements = InlineBody(site, arguments, dropped);
                    statements.Insert(0, Local(site.Called.ReturnType, dropped, DefaultExpression(ParseTypeName(site.Called.ReturnType.ToDisplayString(Symbols.Fqn)))));
                    return Block(statements);
                });
            }
            catch (NotInlined why)
            {
                _prefix.RemoveRange(mark, _prefix.Count - mark);
                if (owner.Folding > 0 || RecordedHelper(site)) throw;
                if (site.View)
                {
                    owner.Fail($"{Callee(site)}, {why.Reason}");
                    return null;
                }

                return node is InvocationExpressionSyntax call
                    ? SyntaxFactory.ExpressionStatement(Left((ExpressionSyntax)base.VisitInvocationExpression(call)!, Why(site, why)))
                    : null;
            }
        }

        /// <summary>
        /// A value of a callee: one expression where its body is one (no statement before), else a local the body
        /// assigns in statements before the current one; or the call left in place (annotated) when neither can be.
        /// </summary>
        private ExpressionSyntax InlineValue(ExpressionSyntax node, CallSite site, SeparatedSyntaxList<ArgumentSyntax> arguments, System.Func<ExpressionSyntax> asWritten)
        {
            if (site.Method.ReturnsVoid)
            {
                if (site.View) owner.Fail($"it calls view method {site.Method.ContainingType.Name}.{site.Method.Name} inside an expression");
                return asWritten();
            }

            int mark = _prefix?.Count ?? 0;
            try
            {
                return Enter(site, () => Expression(node, site, arguments) ?? Hoisted(node, site, arguments)).WithTriviaFrom(node);
            }
            catch (NotInlined why)
            {
                _prefix?.RemoveRange(mark, _prefix.Count - mark);
                if (site.View)
                {
                    owner.Fail($"{Callee(site)}, {why.Reason}");
                    return node;
                }

                if (owner.Folding > 0 || RecordedHelper(site)) throw;
                return Left(asWritten(), Why(site, why));
            }
        }

        /// <summary>
        /// A hidden helper of a recorded body (a local function of its text): it exists only there, so a call of it the
        /// inliner cannot take leaves the whole recorded call in place instead.
        /// </summary>
        private static bool RecordedHelper(CallSite site) =>
            site.Method.MethodKind == MethodKind.LocalFunction && site.Method.Name.StartsWith(Recorded.Helper, System.StringComparison.Ordinal);

        private NotInlined Why(CallSite site, NotInlined why) => new($"{Callee(site)}, {why.Reason}", why.Fixable);

        /// <summary>Inside the callee: refused, too deep or recursive calls throw before its body is read.</summary>
        private T Enter<T>(CallSite site, System.Func<T> inline)
        {
            var callee = owner.Callees.Get(site.Method, owner.Token);
            if (callee.Refusal != null) throw new NotInlined(callee.Refusal);
            if (owner.Stack.Any(m => SymbolEqualityComparer.Default.Equals(m, site.Method))) throw new NotInlined("which is recursive");
            if (owner.Stack.Count >= MaxInlining) throw new NotInlined($"more than {MaxInlining} helpers deep");
            owner.Stack.Add(site.Method);
            try
            {
                var result = inline();
                owner.Count++;
                return result;
            }
            finally
            {
                owner.Stack.RemoveAt(owner.Stack.Count - 1);
            }
        }

        /// <summary>
        /// The callee's value as one expression, in parentheses: its body is one expression, or <c>if</c> / <c>else</c>
        /// and returns only (as <c>?:</c>); its parameters are read, never written or passed by reference, and each
        /// argument is free of writes (it may be evaluated more than once, or not at all). Null: not of that form.
        /// </summary>
        private ExpressionSyntax? Expression(ExpressionSyntax node, CallSite site, SeparatedSyntaxList<ArgumentSyntax> arguments)
        {
            var callee = owner.Callees.Get(site.Method, owner.Token);
            var facts = callee.Facts(owner.Token);
            if (facts.Written.Count > 0 || facts.WritesThis || site.Method.Parameters.Any(p => p.RefKind is RefKind.Ref or RefKind.Out)) return null;
            SyntaxList<StatementSyntax>? statements = callee.Expression != null ? null : callee.Block!.Statements;
            if (statements != null && !Foldable(statements.Value, 0)) return null;
            var saved = _prefix;
            _prefix = null; // nothing may go before the statement: the arguments become expressions too
            owner.Folding++;
            try
            {
                var inner = Bind(site, arguments, callee, null);
                owner.Reserve(callee.Expression ?? (SyntaxNode)callee.Block!);
                var rewriter = new Rewriter(owner, inner);
                var value = callee.Expression != null ? rewriter.Converted(callee.Expression, site) : rewriter.Fold(statements!.Value, 0, site);
                return ParenthesizedExpression(value);
            }
            catch (NotInlined) when (saved != null)
            {
                return null; // the form with statements before may still do
            }
            finally
            {
                _prefix = saved;
                owner.Folding--;
            }
        }

        /// <summary><paramref name="expression"/> rewritten here, converted to the callee's return type where its own type differs.</summary>
        private ExpressionSyntax Converted(ExpressionSyntax expression, CallSite site)
        {
            var value = (ExpressionSyntax)Visit(expression)!;
            var returns = site.Called.ReturnType;
            var type = scope.Model.GetTypeInfo(expression, owner.Token).Type;
            // A target-typed value (new(...), default) takes its type from the return; in the caller it may stand where
            // nothing gives one ('var cell = new(...)' has none), so it names the return type: new T(...), default(T).
            switch (value)
            {
                case ImplicitObjectCreationExpressionSyntax created:
                    return ObjectCreationExpression(Token(SyntaxKind.NewKeyword).WithTrailingTrivia(Space), ParseTypeName(returns.ToDisplayString(Symbols.Fqn)), created.ArgumentList, created.Initializer);
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.DefaultLiteralExpression):
                    return DefaultExpression(ParseTypeName(returns.ToDisplayString(Symbols.Fqn)));
            }
            return SymbolEqualityComparer.Default.Equals(type, site.Method.ReturnType) || type == null
                ? value
                : CastExpression(ParseTypeName(returns.ToDisplayString(Symbols.Fqn)), ParenthesizedExpression(value));
        }

        /// <summary>A body made of <c>if</c> / <c>else</c> and returns only, from statement <paramref name="i"/> on.</summary>
        private static bool Foldable(SyntaxList<StatementSyntax> list, int i)
        {
            if (i >= list.Count) return false;
            return list[i] switch
            {
                ReturnStatementSyntax { Expression: not null } => true,
                BlockSyntax b => b.Statements.Count > 0 && Foldable(b.Statements, 0) && (AlwaysReturns(b) || Foldable(list, i + 1)),
                IfStatementSyntax f => AlwaysReturns(f.Statement) && Foldable(Single(f.Statement), 0)
                    && (f.Else is { } e ? Foldable(Single(e.Statement), 0) && (AlwaysReturns(e.Statement) || Foldable(list, i + 1)) : Foldable(list, i + 1)),
                _ => false,
            };
        }

        private static SyntaxList<StatementSyntax> Single(StatementSyntax s) => s is BlockSyntax b ? b.Statements : SingletonList(s);

        /// <summary>The value of a <see cref="Foldable"/> body from statement <paramref name="i"/> on, as nested <c>?:</c>.</summary>
        private ExpressionSyntax Fold(SyntaxList<StatementSyntax> list, int i, CallSite site)
        {
            switch (list[i])
            {
                case ReturnStatementSyntax { Expression: { } value }:
                    return Converted(value, site);
                case BlockSyntax b:
                    return AlwaysReturns(b) ? Fold(b.Statements, 0, site) : throw new NotInlined("its body cannot be read as one value");
                case IfStatementSyntax f:
                    var condition = (ExpressionSyntax)Visit(f.Condition)!;
                    var whenTrue = Fold(Single(f.Statement), 0, site);
                    var whenFalse = f.Else is { } e && AlwaysReturns(e.Statement) ? Fold(Single(e.Statement), 0, site) : Fold(list, i + 1, site);
                    return ParenthesizedExpression(ConditionalExpression(condition, whenTrue, whenFalse));
                default:
                    throw new NotInlined("its body cannot be read as one value");
            }
        }

        /// <summary>
        /// The callee's value as a local the statements before the current one assign (<c>T __r = default;</c>, then the
        /// body with each <c>return e;</c> an assignment). A callee that changes what the caller sees is inlined so only
        /// where its call is the whole of the statement (an initializer, an assignment's value, an <c>if</c>'s condition),
        /// since its writes would otherwise move before what the statement evaluates first.
        /// </summary>
        private ExpressionSyntax Hoisted(ExpressionSyntax node, CallSite site, SeparatedSyntaxList<ArgumentSyntax> arguments)
        {
            if (_prefix == null) throw new NotInlined("inside an expression the vector loop cannot split: assign it to a local first", true);
            var facts = owner.Callees.Get(site.Method, owner.Token).Facts(owner.Token);
            if (facts.Recursive) throw new NotInlined("which is recursive", false);
            // A body that calls code no loop takes stays scalar wherever the call stands: say so, not how to move it.
            if (!facts.Pure && facts.Opaque != null && !WholeOfStatement(node))
                throw new NotInlined($"which calls {facts.Opaque}", false);
            if (!facts.Pure && !WholeOfStatement(node))
                throw new NotInlined("which changes its arguments or its entity, inside an expression: call it in a statement of its own (var r = ...;)", true);
            if (_statement != null && _statement.DescendantNodes().OfType<SingleVariableDesignationSyntax>().Any(d => d.SpanStart < node.SpanStart && d.Parent is not DeclarationExpressionSyntax))
                throw new NotInlined("after a pattern of the same statement declares a variable: call it in a statement of its own", true);
            string result = owner.Fresh($"__{Callees.NameOf(site.Method)}");
            var type = ParseTypeName(site.Called.ReturnType.ToDisplayString(Symbols.Fqn));
            _prefix.Add(LocalDeclarationStatement(VariableDeclaration(type, SingletonSeparatedList(VariableDeclarator(Identifier(result))
                .WithInitializer(EqualsValueClause(DefaultExpression(type)))))));
            _prefix.Add(Block(InlineBody(site, arguments, result)));
            return IdentifierName(result);
        }

        /// <summary>The call is all a statement evaluates besides a plain target: an initializer, an assignment's value, an if's condition.</summary>
        private static bool WholeOfStatement(ExpressionSyntax node)
        {
            SyntaxNode at = node;
            while (at.Parent is ParenthesizedExpressionSyntax) at = at.Parent;
            return at.Parent switch
            {
                EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1, Parent: LocalDeclarationStatementSyntax } } } => true,
                AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Parent: ExpressionStatementSyntax } a => a.Right == at && Reads(a.Left),
                IfStatementSyntax f => f.Condition == at,
                ExpressionStatementSyntax => true,
                _ => false,
            };
        }

        /// <summary>
        /// The callee's statements, rewritten in a scope of its own (its parameters bound, locals for arguments first in
        /// <paramref name="prefix"/>), with its returns lowered (<see cref="Lower"/>): each <c>return e;</c> an assignment
        /// of <paramref name="result"/>. Locals for its arguments come first.
        /// </summary>
        private List<StatementSyntax> InlineBody(CallSite site, SeparatedSyntaxList<ArgumentSyntax> arguments, string? result)
        {
            var callee = owner.Callees.Get(site.Method, owner.Token);
            var locals = new List<StatementSyntax>();
            var inner = Bind(site, arguments, callee, locals);
            owner.Reserve(callee.Expression ?? (SyntaxNode)callee.Block!);
            var rewriter = new Rewriter(owner, inner);
            var statements = new List<StatementSyntax>();
            if (callee.Expression != null)
            {
                statements.AddRange(rewriter.Arrow(callee.Expression, site.Method.ReturnsVoid ? null : result));
            }
            else
            {
                foreach (var statement in callee.Block!.Statements) statements.AddRange(rewriter.Statements(statement));
            }

            var exit = new Exit(result);
            var lowered = Lower(statements, exit);
            if (exit.Live != null)
                lowered.Insert(0, LocalDeclarationStatement(VariableDeclaration(PredefinedType(Token(SyntaxKind.BoolKeyword)),
                    SingletonSeparatedList(VariableDeclarator(Identifier(exit.Live)).WithInitializer(EqualsValueClause(LiteralExpression(SyntaxKind.TrueLiteralExpression)))))));
            locals.AddRange(lowered);
            return locals;
        }

        // ---------------- binding ----------------

        /// <summary>
        /// The callee's scope: each parameter bound to its argument (rewritten here, in order): a variable for
        /// <c>ref</c>, <c>out</c> and <c>in</c>, the argument itself where it is a plain read of the parameter's type that
        /// the body never writes, else (with <paramref name="locals"/>) a local of the parameter's type, or (without) the
        /// argument converted, when it writes nothing; <c>this</c> to the receiver; the type parameters to the call's
        /// type arguments.
        /// </summary>
        private Scope Bind(CallSite site, SeparatedSyntaxList<ArgumentSyntax> arguments, Callee callee, List<StatementSyntax>? locals)
        {
            var method = site.Method;
            var facts = callee.Facts(owner.Token);
            var names = new Dictionary<ISymbol, ExpressionSyntax>(SymbolEqualityComparer.Default);
            for (int i = 0; i < callee.TypeParameters.Length; i++)
                names[callee.TypeParameters[i]] = ParseTypeName(site.Called.TypeArguments[i].ToDisplayString(Symbols.Fqn));
            for (var (d, c) = (method.ContainingType.OriginalDefinition, site.Called.ContainingType); d != null && c != null; (d, c) = (d.ContainingType, c.ContainingType))
                for (int i = 0; i < d.TypeParameters.Length && i < c.TypeArguments.Length; i++)
                    if (!SymbolEqualityComparer.Default.Equals(d.TypeParameters[i], c.TypeArguments[i]))
                        names[d.TypeParameters[i]] = ParseTypeName(c.TypeArguments[i].ToDisplayString(Symbols.Fqn));

            ExpressionSyntax? self = null;
            bool viewThis = site.View, thisWritable = true;
            if (!site.View && !method.IsStatic && method.MethodKind != MethodKind.LocalFunction || method.ContainingType.IsExtension && !method.IsStatic)
            {
                // The receiver: written as it is (a variable), or the receiver of this scope where the call names none.
                if (site.Receiver == null || site.Receiver is ThisExpressionSyntax)
                {
                    if (scope.ViewThis) viewThis = true;
                    else if (scope.This != null) (self, thisWritable) = (scope.This, scope.ThisWritable);
                    else throw new NotInlined("without a receiver the vector loop can follow");
                }
                else
                {
                    self = InCaller(site.Receiver);
                    thisWritable = Writable(site.Receiver);
                }

                if (callee.Receiver != null && facts.Written.Contains(callee.Receiver) && !thisWritable)
                    throw new NotInlined($"which writes its struct, on a receiver it may not write ('{site.Receiver}'): a copy would be written; take it by ref");
                if (method.ContainingType.IsExtension || callee.Receiver != null)
                {
                    // An extension's receiver, or a recorded body's (its first parameter, named 'this'): bound as a parameter.
                    var parameter = callee.Receiver ?? method.ContainingType.ExtensionParameter!;
                    names[parameter] = Value(parameter, site.Receiver, self!, facts, locals, receiverWritable: thisWritable);
                    self = null;
                }
                else if (facts.WritesThis && !thisWritable)
                    throw new NotInlined($"which writes its struct, on a receiver it may not write ('{site.Receiver}'): a copy would be written; take it by ref");
                else if (!Reads(self!))
                {
                    if (facts.WritesThis || locals == null && !Pure(site.Receiver!)) throw new NotInlined("on a receiver that is not a variable: assign it to a local first", true);
                    if (locals != null)
                    {
                        string copy = owner.Fresh("__this");
                        locals.Add(Local(method.ContainingType, copy, self!));
                        self = IdentifierName(copy);
                    }
                    else self = ParenthesizedExpression(self!);
                }
            }

            // The parameters the body names (a recorded body's own), placed by the member's declaration (same order and names).
            var parameters = callee.Parameters;
            var declared = method.Parameters;
            int offset = site.ReceiverIsArgument ? 1 : 0;
            if (site.ReceiverIsArgument)
            {
                var value = InCaller(site.Receiver!);
                names[parameters[0]] = Value(parameters[0], site.Receiver, value, facts, locals, receiverWritable: Writable(site.Receiver!));
            }

            var given = new ArgumentSyntax?[parameters.Length];
            for (int i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];
                int index = argument.NameColon is { } named ? IndexOf(declared, named.Name.Identifier.ValueText) : i + offset;
                if (index < 0 || index >= parameters.Length || declared[index].IsParams)
                    throw new NotInlined("with a params array or an argument the vector loop cannot place");
                given[index] = argument;
            }

            for (int i = offset; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (given[i] is not { } argument)
                {
                    if (!declared[i].HasExplicitDefaultValue || DefaultLiteral(declared[i]) is not { } literal)
                        throw new NotInlined($"leaving out '{parameter.Name}', whose default the vector loop cannot write");
                    names[parameter] = literal;
                    continue;
                }

                if (parameter.RefKind is RefKind.Out && argument.Expression is DeclarationExpressionSyntax declaration)
                {
                    if (locals == null) throw new NotInlined("declaring a variable inside an expression", true);
                    string name = declaration.Designation is SingleVariableDesignationSyntax single ? single.Identifier.ValueText : owner.Fresh("__out");
                    if (declaration.Designation is SingleVariableDesignationSyntax && _prefix == null) throw new NotInlined("declaring a variable inside an expression", true);
                    (_prefix ?? locals).Add(Local(parameter.Type, name, DefaultExpression(ParseTypeName(parameter.Type.ToDisplayString(Symbols.Fqn)))));
                    names[parameter] = IdentifierName(name);
                    continue;
                }

                if (argument.Expression is IdentifierNameSyntax { Identifier.ValueText: "_" } && parameter.RefKind == RefKind.Out && Bound(argument.Expression) == null)
                {
                    if (locals == null) throw new NotInlined("discarding an out value inside an expression", true);
                    string discard = owner.Fresh("__discard");
                    locals.Add(Local(parameter.Type, discard, DefaultExpression(ParseTypeName(parameter.Type.ToDisplayString(Symbols.Fqn)))));
                    names[parameter] = IdentifierName(discard);
                    continue;
                }

                var rewritten = InCaller(argument.Expression);
                names[parameter] = Value(parameter, argument.Expression, rewritten, facts, locals, receiverWritable: true);
            }

            return new Scope(callee.Model, method, names) { This = self, ViewThis = viewThis, ThisWritable = thisWritable };
        }

        /// <summary>
        /// A receiver or an argument rewritten in the caller's place: the callee being bound is not yet entered, so a call
        /// of it there (<c>Grow(Grow(x))</c>) is a call after it, not a recursive one.
        /// </summary>
        private ExpressionSyntax InCaller(ExpressionSyntax expression)
        {
            var stack = owner.Stack;
            if (stack.Count == 0) return (ExpressionSyntax)Visit(expression)!;
            var entered = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            try
            {
                return (ExpressionSyntax)Visit(expression)!;
            }
            finally
            {
                stack.Add(entered);
            }
        }

        private static int IndexOf(System.Collections.Immutable.ImmutableArray<IParameterSymbol> parameters, string name)
        {
            for (int i = 0; i < parameters.Length; i++)
                if (parameters[i].Name == name) return i;
            return -1;
        }

        /// <summary>
        /// What a parameter becomes: the caller's variable (by reference), the argument itself (a plain read of the
        /// parameter's type, never written), a local holding it (<paramref name="locals"/>), or the argument converted.
        /// </summary>
        private ExpressionSyntax Value(IParameterSymbol parameter, ExpressionSyntax? original, ExpressionSyntax value, Effects facts,
            List<StatementSyntax>? locals, bool receiverWritable)
        {
            bool written = facts.Written.Contains(parameter);
            if (parameter.RefKind is RefKind.Ref or RefKind.Out or RefKind.In or RefKind.RefReadOnlyParameter)
            {
                if (Reads(value) && (parameter.RefKind is RefKind.In or RefKind.RefReadOnlyParameter || receiverWritable || !written)) return value;
                if (parameter.RefKind is RefKind.In or RefKind.RefReadOnlyParameter && !written) { } // an rvalue passed to 'in': a copy, below
                else throw new NotInlined($"passing '{original}' by reference, which is not a variable the vector loop can name", true);
            }

            bool same = original != null && SymbolEqualityComparer.Default.Equals(scope.Model.GetTypeInfo(original, owner.Token).Type, parameter.Type);
            // A plain read in place of the parameter, as the method written by hand reads it: a local would hide from the
            // vector loop that each field of a component gets the same operation (flat lanes).
            if (!written && Reads(value)) return same ? value : Converted(value, parameter.Type);
            if (locals != null)
            {
                string name = owner.Fresh($"__{parameter.Name}");
                locals.Add(Local(parameter.Type, name, value));
                return IdentifierName(name);
            }

            if (written) throw new NotInlined($"which writes its parameter '{parameter.Name}'");
            if (original != null && !Pure(original)) throw new NotInlined("with an argument that writes or calls: assign it to a local first", true);
            return same ? ParenthesizedExpression(value) : Converted(value, parameter.Type);
        }

        private static ExpressionSyntax Converted(ExpressionSyntax value, ITypeSymbol type) =>
            ParenthesizedExpression(CastExpression(ParseTypeName(type.ToDisplayString(Symbols.Fqn)), ParenthesizedExpression(value)));

        private static LocalDeclarationStatementSyntax Local(ITypeSymbol type, string name, ExpressionSyntax value) =>
            LocalDeclarationStatement(VariableDeclaration(ParseTypeName(type.ToDisplayString(Symbols.Fqn)),
                SingletonSeparatedList(VariableDeclarator(Identifier(name)).WithInitializer(EqualsValueClause(value)))));

        private static ExpressionSyntax? DefaultLiteral(IParameterSymbol parameter) => parameter.ExplicitDefaultValue switch
        {
            null => DefaultExpression(ParseTypeName(parameter.Type.ToDisplayString(Symbols.Fqn))),
            bool b => LiteralExpression(b ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
            float or double or int or uint or long or ulong or short or ushort or byte or sbyte when parameter.Type.SpecialType != SpecialType.None =>
                CastExpression(ParseTypeName(parameter.Type.ToDisplayString(Symbols.Fqn)),
                    ParenthesizedExpression(ParseExpression(System.Convert.ToString(parameter.ExplicitDefaultValue, System.Globalization.CultureInfo.InvariantCulture)
                        + parameter.ExplicitDefaultValue switch { float => "f", double => "d", _ => "" }))),
            _ => null,
        };

        /// <summary>
        /// An expression that reads a variable or a value without any effect: a name, a literal, <c>this</c>, or a chain of
        /// member reads of one (in parentheses or not). Bound to the same place wherever it is written.
        /// </summary>
        private static bool Reads(ExpressionSyntax e) => e switch
        {
            IdentifierNameSyntax or LiteralExpressionSyntax or ThisExpressionSyntax => true,
            MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } access => Reads(access.Expression),
            ParenthesizedExpressionSyntax parenthesized => Reads(parenthesized.Expression),
            _ => false,
        };

        /// <summary>Whether the receiver of a struct member is a variable it may write (not an 'in' parameter, a readonly field or a value).</summary>
        private bool Writable(ExpressionSyntax receiver)
        {
            if (receiver is ThisExpressionSyntax) return scope.ThisWritable;
            var op = scope.Model.GetOperation(receiver, owner.Token);
            while (op is IFieldReferenceOperation { Instance: { } instance } f)
            {
                if (f.Field.IsReadOnly) return false;
                op = instance;
            }
            return op switch
            {
                IParameterReferenceOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out or RefKind.None } => true,
                ILocalReferenceOperation { Local: { IsRef: false } or { RefKind: RefKind.Ref } } => true,
                IInstanceReferenceOperation => scope.ThisWritable,
                _ => false,
            };
        }

        /// <summary>
        /// The argument writes nothing and calls nothing but math and pure helpers: it may be evaluated in place of the
        /// parameter, more than once or not at all.
        /// </summary>
        private bool Pure(ExpressionSyntax argument)
        {
            var op = scope.Model.GetOperation(argument, owner.Token);
            if (op == null) return false;
            foreach (var o in op.DescendantsAndSelf())
                switch (o)
                {
                    case IAssignmentOperation or IIncrementOrDecrementOperation or IInvalidOperation:
                    case IInvocationOperation call when !IsMath(call.TargetMethod) && !KnownVector(call.TargetMethod.ContainingType) && Recorded.AccessOf(call.TargetMethod) == null
                        && !(owner.Callees.Candidate(call.TargetMethod) is { } definition && owner.Callees.Get(definition, owner.Token).Facts(owner.Token).Pure):
                    case IPropertyReferenceOperation access when Symbols.IsGenerated(access.Property)
                        || owner.Callees.Candidate(access.Property.GetMethod) is { } getter && !owner.Callees.Get(getter, owner.Token).Facts(owner.Token).Pure:
                        return false;
                }
            return true;
        }

        private static ExpressionSyntax Parenthesize(ExpressionSyntax e) => Reads(e) && e is not LiteralExpressionSyntax ? e : ParenthesizedExpression(e);

        // ---------------- returns ----------------

        /// <summary>Where a callee's returns go: the local of its value (null: none), and the flag of the lanes still running.</summary>
        private sealed class Exit(string? result)
        {
            public string? Result { get; } = result;
            public string? Live { get; set; }
            public bool Flagged { get; set; }
        }

        private static bool HasReturn(StatementSyntax s) => s.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any();

        private static bool AlwaysReturns(StatementSyntax s) => s switch
        {
            ReturnStatementSyntax => true,
            BlockSyntax b => b.Statements.Any(AlwaysReturns),
            IfStatementSyntax { Else: { } e } f => AlwaysReturns(f.Statement) && AlwaysReturns(e.Statement),
            _ => false,
        };

        /// <summary>
        /// The callee's statements without returns: after an <c>if</c> whose one branch returns on every path and the
        /// other never, the rest of the body goes into the other branch; where a branch returns on some paths only, a
        /// flag (<c>bool __live = true;</c>, cleared by each return) guards the rest. A <c>return e;</c> assigns the result.
        /// </summary>
        private List<StatementSyntax> Lower(List<StatementSyntax> list, Exit exit)
        {
            var lowered = new List<StatementSyntax>();
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (!HasReturn(s))
                {
                    lowered.Add(s);
                    continue;
                }

                var rest = list.GetRange(i + 1, list.Count - i - 1);
                switch (s)
                {
                    case ReturnStatementSyntax r:
                        if (r.Expression != null) lowered.Add(Assign(exit.Result!, r.Expression));
                        if (exit.Flagged) lowered.Add(Assign(exit.Live!, LiteralExpression(SyntaxKind.FalseLiteralExpression)));
                        return lowered;
                    case BlockSyntax b:
                        lowered.AddRange(Lower([.. b.Statements, .. rest], exit));
                        return lowered;
                    case IfStatementSyntax f:
                    {
                        var (a, b) = (f.Statement, f.Else?.Statement);
                        bool aAll = AlwaysReturns(a), bAll = b != null && AlwaysReturns(b);
                        if (aAll && (b == null || !HasReturn(b)))
                            lowered.Add(Branches(f, Lower([a], exit), Lower(b == null ? rest : [b, .. rest], exit)));
                        else if (bAll && !HasReturn(a))
                            lowered.Add(Branches(f, Lower([a, .. rest], exit), Lower([b!], exit)));
                        else if (aAll && bAll)
                            lowered.Add(Branches(f, Lower([a], exit), Lower([b!], exit)));
                        else
                        {
                            // A branch returns on some paths only: the lanes that go on are a flag's.
                            bool flagged = exit.Flagged;
                            exit.Live ??= owner.Fresh("__live");
                            exit.Flagged = true;
                            lowered.Add(Branches(f, Lower([a], exit), b == null ? null : Lower([b], exit)));
                            if (rest.Count > 0) lowered.Add(IfStatement(IdentifierName(exit.Live), Block(Lower(rest, exit))));
                            exit.Flagged = flagged;
                        }
                        return lowered;
                    }
                    case SwitchStatementSyntax select: // Kernel.Switches.cs
                        lowered.AddRange(LowerSwitch(select, rest, exit));
                        return lowered;
                    default:
                        throw new NotInlined("which returns from inside a statement the vector loop cannot take apart");
                }
            }

            return lowered;
        }

        private static IfStatementSyntax Branches(IfStatementSyntax f, List<StatementSyntax> then, List<StatementSyntax>? otherwise) =>
            f.WithStatement(Block(then)).WithElse(otherwise is { Count: > 0 } ? ElseClause(Block(otherwise)) : null);

        private static StatementSyntax Assign(string name, ExpressionSyntax value) =>
            SyntaxFactory.ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName(name), value));
    }
}
