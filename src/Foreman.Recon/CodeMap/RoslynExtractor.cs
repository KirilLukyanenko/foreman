using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Foreman.Recon.CodeMap;

/// <summary>A .NET project as its file says, read as XML: nothing is evaluated, restored or built.</summary>
public sealed record DotnetProject(
    string Path,
    string Name,
    IReadOnlyList<string> TargetFrameworks,
    string? OutputType,
    bool IsTest,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    int SourceFiles);

/// <summary>
/// C# definitions and references from Roslyn, with every reference resolved to the exact symbol it means.
/// Safe on untrusted code because nothing runs: project files are read as plain XML (no MSBuild evaluation, which
/// can execute tasks), and each project is a compilation of its own source files against the framework and the
/// projects it references. No analyzers or source generators are loaded, NuGet packages are not restored, and
/// nothing is emitted. Unresolved names (from packages) are kept by name, as tree-sitter would.
/// </summary>
public static partial class RoslynExtractor
{
    private const int MaxSignature = 160;

    private static readonly string[] ImplicitUsings =
        ["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks"];

    private static readonly Lazy<IReadOnlyList<MetadataReference>> Framework = new(() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => System.IO.Path.GetFileName(p).StartsWith("System", StringComparison.Ordinal) || System.IO.Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList());

    public static (IReadOnlyList<FileTags> Files, IReadOnlyList<DotnetProject> Projects) Extract(
        string root, IReadOnlyList<string> csFiles, IReadOnlyList<string> projectFiles, CancellationToken ct = default)
    {
        var projects = projectFiles.Select(p => ReadProject(root, p)).ToList();
        // Each file belongs to the project in its nearest enclosing folder (SDK-style projects include their folder).
        var owner = csFiles.ToLookup(f => projects
            .Where(p => IsUnder(f, ProjectDir(p.Path)))
            .MaxBy(p => ProjectDir(p.Path).Length)?.Path ?? "");

        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithDocumentationMode(DocumentationMode.None);
        var compilations = new Dictionary<string, CSharpCompilation>();
        var results = new ConcurrentBag<FileTags>();
        var counts = new Dictionary<string, int>();

        foreach (var project in Ordered(projects).Append(null))
        {
            ct.ThrowIfCancellationRequested();
            var key = project?.Path ?? "";
            var files = owner[key].ToList();
            counts[key] = files.Count;
            if (files.Count == 0) continue;

            var trees = files
                .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(System.IO.Path.Combine(root, f)), parseOptions, path: f, cancellationToken: ct))
                .Append(CSharpSyntaxTree.ParseText(string.Join("\n", ImplicitUsings.Select(u => $"global using {u};")), parseOptions, path: "<implicit usings>"))
                .ToList();
            var references = Framework.Value.Concat(
                (project?.ProjectReferences ?? []).Where(compilations.ContainsKey).Select(r => compilations[r].ToMetadataReference()));
            var compilation = CSharpCompilation.Create(
                System.IO.Path.GetFileNameWithoutExtension(key.Length == 0 ? "loose" : key),
                trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
            compilations[key] = compilation;

            Parallel.ForEach(trees.Where(t => t.FilePath != "<implicit usings>"), new ParallelOptions { CancellationToken = ct }, tree =>
            {
                try
                {
                    results.Add(ExtractFile(tree, compilation.GetSemanticModel(tree), ct));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    results.Add(new FileTags(tree.FilePath, CodeLanguages.CSharp, 0, [], [], $"{e.GetType().Name}: {e.Message}"));
                }
            });
        }

        return (results.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
                projects.Select(p => p with { SourceFiles = counts.GetValueOrDefault(p.Path) }).ToList());
    }

    internal static FileTags ExtractFile(SyntaxTree tree, SemanticModel model, CancellationToken ct)
    {
        var root = tree.GetRoot(ct);
        var text = tree.GetText(ct);
        var defs = new List<CodeSymbol>();
        foreach (var node in root.DescendantNodes(n => n is not BlockSyntax and not ArrowExpressionClauseSyntax))
        {
            foreach (var (name, kind, at) in Declared(node))
            {
                var id = (model.GetDeclaredSymbol(at, ct) ?? (at is VariableDeclaratorSyntax v ? model.GetDeclaredSymbol(v, ct) : null))
                    ?.OriginalDefinition.GetDocumentationCommentId();
                var line = text.Lines.GetLineFromPosition(at is VariableDeclaratorSyntax vd ? vd.Identifier.SpanStart : NameToken(node).SpanStart);
                defs.Add(new CodeSymbol(name, kind, line.LineNumber + 1, text.Lines.GetLineFromPosition(node.Span.End).LineNumber + 1,
                    Signature(node, text), id));
            }
        }

        var refs = new Dictionary<(string Name, string? Id), int>();
        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var info = model.GetSymbolInfo(name, ct);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            string? id = null;
            if (symbol is not null)
            {
                if (symbol is IMethodSymbol { ReducedFrom: { } reduced }) symbol = reduced;
                var original = symbol.OriginalDefinition;
                // Framework symbols are not part of the repository's graph.
                if (!original.Locations.Any(l => l.IsInSource)) continue;
                id = original.GetDocumentationCommentId();
            }
            var key = (name.Identifier.ValueText, id);
            refs[key] = refs.GetValueOrDefault(key) + 1;
        }

        return new FileTags(tree.FilePath, CodeLanguages.CSharp, text.Lines.Count,
            defs.OrderBy(d => d.Line).ToList(),
            refs.Select(r => new CodeRef(r.Key.Name, r.Value, r.Key.Id)).OrderBy(r => r.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>The declarations a node introduces: one for most members, one per variable in a field declaration.</summary>
    private static IEnumerable<(string Name, string Kind, SyntaxNode At)> Declared(SyntaxNode node) => node switch
    {
        ClassDeclarationSyntax c => [(c.Identifier.ValueText, "class", c)],
        StructDeclarationSyntax s => [(s.Identifier.ValueText, "struct", s)],
        InterfaceDeclarationSyntax i => [(i.Identifier.ValueText, "interface", i)],
        RecordDeclarationSyntax r => [(r.Identifier.ValueText, "record", r)],
        EnumDeclarationSyntax e => [(e.Identifier.ValueText, "enum", e)],
        DelegateDeclarationSyntax d => [(d.Identifier.ValueText, "delegate", d)],
        MethodDeclarationSyntax m => [(m.Identifier.ValueText, "method", m)],
        ConstructorDeclarationSyntax c => [(c.Identifier.ValueText, "constructor", c)],
        PropertyDeclarationSyntax p => [(p.Identifier.ValueText, "property", p)],
        EventDeclarationSyntax e => [(e.Identifier.ValueText, "event", e)],
        EnumMemberDeclarationSyntax e => [(e.Identifier.ValueText, "enum_member", e)],
        FieldDeclarationSyntax f => f.Declaration.Variables.Select(v => (v.Identifier.ValueText, f.Modifiers.Any(SyntaxKind.ConstKeyword) ? "constant" : "field", (SyntaxNode)v)),
        EventFieldDeclarationSyntax e => e.Declaration.Variables.Select(v => (v.Identifier.ValueText, "event", (SyntaxNode)v)),
        _ => [],
    };

    private static SyntaxToken NameToken(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier,
        DelegateDeclarationSyntax d => d.Identifier,
        MethodDeclarationSyntax m => m.Identifier,
        ConstructorDeclarationSyntax c => c.Identifier,
        PropertyDeclarationSyntax p => p.Identifier,
        EventDeclarationSyntax e => e.Identifier,
        EnumMemberDeclarationSyntax e => e.Identifier,
        _ => node.GetFirstToken(),
    };

    /// <summary>The declaration without attributes and body, on one line, indented as in the file.</summary>
    internal static string Signature(SyntaxNode node, Microsoft.CodeAnalysis.Text.SourceText text)
    {
        var start = node is MemberDeclarationSyntax { AttributeLists.Count: > 0 } member
            ? member.AttributeLists.Last().GetLastToken().GetNextToken().SpanStart
            : node.SpanStart;
        var end = node switch
        {
            BaseTypeDeclarationSyntax { OpenBraceToken.IsMissing: false } t when t.OpenBraceToken.SpanStart > 0 => t.OpenBraceToken.SpanStart,
            BaseMethodDeclarationSyntax m => m.Body?.SpanStart ?? m.ExpressionBody?.SpanStart ?? m.Span.End,
            PropertyDeclarationSyntax p => p.AccessorList?.SpanStart ?? p.ExpressionBody?.SpanStart ?? p.Span.End,
            EventDeclarationSyntax e => e.AccessorList?.SpanStart ?? e.Span.End,
            _ => node.Span.End,
        };
        if (end < start) end = node.Span.End;
        var line = text.Lines.GetLineFromPosition(start);
        var lineText = line.ToString();
        var indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];
        var body = Whitespace().Replace(text.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(start, end)), " ").Trim();
        if (node is FieldDeclarationSyntax or EventFieldDeclarationSyntax || node.Parent is VariableDeclarationSyntax)
            body = body.TrimEnd(';');
        var signature = indent + body;
        return signature.Length <= MaxSignature ? signature : signature[..(MaxSignature - 1)] + "…";
    }

    // ------------------------------------------------------------------ project files

    internal static DotnetProject ReadProject(string root, string path)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(System.IO.Path.Combine(root, path), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (Exception e) when (e is XmlException or IOException)
        {
            return new DotnetProject(path, System.IO.Path.GetFileNameWithoutExtension(path), [], null, false, [], [], 0);
        }

        IEnumerable<XElement> All(string name) => doc.Descendants().Where(e => e.Name.LocalName == name);
        string? Value(string name) => All(name).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);

        var dir = ProjectDir(path);
        var frameworks = (Value("TargetFrameworks") ?? Value("TargetFramework") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var packages = All("PackageReference").Select(e => (string?)e.Attribute("Include")).OfType<string>().Distinct().ToList();
        var references = All("ProjectReference").Select(e => (string?)e.Attribute("Include")).OfType<string>()
            .Select(r => Normalize(dir.Length == 0 ? r : dir + "/" + r)).Distinct().ToList();
        var isTest = string.Equals(Value("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
            || packages.Any(p => p.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) || p.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) || p.Equals("TUnit", StringComparison.OrdinalIgnoreCase));
        return new DotnetProject(path, System.IO.Path.GetFileNameWithoutExtension(path), frameworks, Value("OutputType"), isTest, references, packages, 0);
    }

    /// <summary>Projects after the projects they reference; a reference cycle is broken where it is found.</summary>
    private static IEnumerable<DotnetProject> Ordered(IReadOnlyList<DotnetProject> projects)
    {
        var byPath = projects.ToDictionary(p => p.Path);
        var done = new HashSet<string>();
        var visiting = new HashSet<string>();
        var order = new List<DotnetProject>();
        void Visit(DotnetProject p)
        {
            if (done.Contains(p.Path) || !visiting.Add(p.Path)) return;
            foreach (var r in p.ProjectReferences)
                if (byPath.TryGetValue(r, out var dep)) Visit(dep);
            visiting.Remove(p.Path);
            done.Add(p.Path);
            order.Add(p);
        }
        foreach (var p in projects) Visit(p);
        return order;
    }

    private static string ProjectDir(string projectPath) => System.IO.Path.GetDirectoryName(projectPath)?.Replace('\\', '/') ?? "";

    private static bool IsUnder(string file, string dir) => dir.Length == 0 || file.StartsWith(dir + "/", StringComparison.Ordinal);

    /// <summary>Resolves <c>..</c> and <c>.</c> in a repository-relative path with forward slashes.</summary>
    internal static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
