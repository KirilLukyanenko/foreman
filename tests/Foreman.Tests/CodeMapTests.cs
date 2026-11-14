using Foreman.Core;
using Foreman.Recon.CodeMap;

namespace Foreman.Tests;

public class CodeMapTests
{
    [Fact]
    public void Roslyn_resolves_references_to_the_exact_symbol_across_projects()
    {
        using var repo = new TempRepo(
            ("src/Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"),
            ("src/Core/Orders.cs", """
                namespace Shop.Core;

                /// <summary>An order.</summary>
                public sealed class Order
                {
                    public decimal Total { get; init; }

                    [Obsolete("old")]
                    public decimal Discounted(decimal rate) => Total * (1 - rate);
                }

                public interface IClock { DateTime Now { get; } }
                """),
            ("src/Core/Other.cs", "namespace Shop.Other;\npublic sealed class Order { public int Id; }\n"),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
                  <ItemGroup><ProjectReference Include="..\Core\Core.csproj" /><PackageReference Include="Serilog" Version="4.0.0" /></ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                using Shop.Core;
                var order = new Order { Total = 10 };
                Console.WriteLine(order.Discounted(0.1m));
                """));

        var cs = new[] { "src/App/Program.cs", "src/Core/Orders.cs", "src/Core/Other.cs" };
        var (files, projects) = RoslynExtractor.Extract(repo.Root, cs, ["src/App/App.csproj", "src/Core/Core.csproj"], TestContext.Current.CancellationToken);

        var app = projects.Single(p => p.Name == "App");
        Assert.Equal(["src/Core/Core.csproj"], app.ProjectReferences);
        Assert.Equal(["Serilog"], app.PackageReferences);
        Assert.Equal("Exe", app.OutputType);
        Assert.Equal(1, app.SourceFiles);

        var orders = files.Single(f => f.Path == "src/Core/Orders.cs");
        var order = orders.Defs.Single(d => d.Name == "Order");
        Assert.Equal("class", order.Kind);
        Assert.Equal(4, order.Line);
        Assert.Equal("T:Shop.Core.Order", order.Id);
        var discounted = orders.Defs.Single(d => d.Name == "Discounted");
        // Attributes and the body are not part of the signature.
        Assert.Equal("    public decimal Discounted(decimal rate)", discounted.Signature);

        // Program.cs uses Shop.Core.Order, not Shop.Other.Order: the reference carries the exact id.
        var program = files.Single(f => f.Path == "src/App/Program.cs");
        Assert.Contains(program.Refs, r => r.Name == "Order" && r.Id == "T:Shop.Core.Order");
        Assert.Contains(program.Refs, r => r.Name == "Discounted" && r.Id == "M:Shop.Core.Order.Discounted(System.Decimal)");
        // Framework symbols are not part of the graph.
        Assert.DoesNotContain(program.Refs, r => r.Name == "WriteLine");
    }

    [Fact]
    public void Exact_references_beat_name_matches_in_the_ranking()
    {
        var index = new CodeIndex([
            new FileTags("app/Program.cs", "csharp", 10, [], [new CodeRef("Order", 3, "T:A.Order")]),
            new FileTags("core/A.cs", "csharp", 10, [new CodeSymbol("Order", "class", 1, 5, "public class Order", "T:A.Order")], []),
            new FileTags("core/B.cs", "csharp", 10, [new CodeSymbol("Order", "class", 1, 5, "public class Order", "T:B.Order")], []),
        ]);
        var map = RepoMap.Build(index, TaskFocus.Empty, 1000);
        var ranks = map.Files.ToDictionary(f => f.Path, f => f.Rank);
        Assert.True(ranks["core/A.cs"] > ranks["core/B.cs"]);
        Assert.Contains("core/A.cs\n    1  public class Order", map.Text);
    }

    [Fact]
    public void The_map_fits_the_budget_and_shows_containing_types()
    {
        var defs = Enumerable.Range(1, 200).Select(i => new CodeSymbol($"Method{i}", "method", i * 2 + 1, i * 2 + 1, $"    public void Method{i}(int value)")).ToList();
        var index = new CodeIndex([
            new FileTags("Big.cs", "csharp", 500, [new CodeSymbol("Big", "class", 1, 500, "public class Big"), .. defs], []),
            new FileTags("User.cs", "csharp", 10, [], Enumerable.Range(1, 200).Select(i => new CodeRef($"Method{i}", 1)).ToList()),
        ]);
        var map = RepoMap.Build(index, TaskFocus.Empty, 300);
        Assert.True(map.Tokens <= 300, $"{map.Tokens} tokens");
        Assert.True(map.SymbolsShown > 5);
        Assert.Contains("    1  public class Big", map.Text);
        Assert.Contains("⋮", map.Text);
    }

    [Fact]
    public void Task_mentions_personalise_the_ranking()
    {
        var index = new CodeIndex([
            new FileTags("src/billing/invoice.py", "python", 50, [new CodeSymbol("render_invoice", "function", 3, 20, "def render_invoice(order):")], [new CodeRef("helper", 1)]),
            new FileTags("src/shipping/label.py", "python", 50, [new CodeSymbol("print_label", "function", 3, 20, "def print_label(order):")], [new CodeRef("helper", 1)]),
            new FileTags("src/util.py", "python", 50, [new CodeSymbol("helper", "function", 1, 5, "def helper():")], []),
        ]);
        var focus = TaskFocusFinder.Find("The total in render_invoice is wrong when the invoice has a discount.", index, FocusMode.Exact);
        Assert.Contains("render_invoice", focus.Identifiers);
        var map = RepoMap.Build(index, focus, 1000);
        var order = map.Files.Select(f => f.Path).ToList();
        Assert.True(order.IndexOf("src/billing/invoice.py") < order.IndexOf("src/shipping/label.py"));
    }

    [Theory]
    [InlineData("parseHTTPHeader_value", new[] { "parse", "http", "header", "value" })]
    [InlineData("naturaldelta", new[] { "naturaldelta" })]
    [InlineData("Utf8JsonReader", new[] { "utf", "8", "json", "reader" })]
    public void Identifiers_split_into_words(string identifier, string[] words) =>
        Assert.Equal(words, TaskFocusFinder.SplitWords(identifier));

    [Fact]
    public void Bm25_ranks_the_file_that_matches_the_rare_words()
    {
        var ranked = Bm25.Rank("timeout exception has the wrong timeout value", new Dictionary<string, string>
        {
            ["TimeoutEngine.cs"] = "class TimeoutEngine { void Throw() => throw new TimeoutRejectedException(timeout); }",
            ["Retry.cs"] = "class RetryEngine { int value; }",
            ["Readme.cs"] = "class Other { }",
        });
        Assert.Equal("TimeoutEngine.cs", ranked[0].Path);
        Assert.Equal(0, ranked[^1].Rank);
    }

    [Theory]
    [InlineData("src/Models/Order.g.cs", "", true)]
    [InlineData("Migrations/20240101_Init.Designer.cs", "", true)]
    [InlineData("api/user.pb.go", "", true)]
    [InlineData("src/Order.cs", "// <auto-generated />\nnamespace X;", true)]
    [InlineData("gen/types.go", "// Code generated by stringer; DO NOT EDIT.\npackage x", true)]
    [InlineData("src/Order.cs", "// do not edit this line by hand, it is fine\nnamespace X;", false)]
    [InlineData("src/Order.cs", "namespace X;", false)]
    public void Generated_files_are_recognised(string path, string head, bool generated) =>
        Assert.Equal(generated, GeneratedFiles.IsGenerated(path, _ => head));

    [Theory]
    [InlineData("tests/test_time.py", true)]
    [InlineData("src/humanize/time.py", false)]
    [InlineData("test/GuardClauses.UnitTests/GuardAgainstNull.cs", true)]
    [InlineData("src/App.Tests/OrderTests.cs", true)]
    [InlineData("src/Latest.cs", false)]
    [InlineData("src/FluentValidation.Tests.Benchmarks/DataSet.cs", true)]
    [InlineData("benchmarks/routers/src/index.ts", true)]
    [InlineData("src/router.test.ts", true)]
    [InlineData("uuid_test.go", true)]
    public void Test_paths_are_recognised(string path, bool test) => Assert.Equal(test, CodePaths.IsTest(path));

    [Theory]
    [InlineData("src/GuardClauses/ThirdParty/JetBrains.Annotations.cs", true)]
    [InlineData("vendor/github.com/x/y.go", true)]
    [InlineData("src/vendors.ts", false)]
    public void Vendored_paths_are_recognised(string path, bool vendored) => Assert.Equal(vendored, CodePaths.IsVendored(path));

    [Fact]
    public void Tree_sitter_lines_parse_into_tags()
    {
        var tags = TreeSitterExtractor.Parse("""{"path": "a/b.go", "lang": "go", "lines": 12, "defs": [["Parse", "function", 3, 9, "func Parse(s string) (UUID, error) {"]], "refs": {"UUID": 2}, "error": null}""");
        Assert.Equal("a/b.go", tags.Path);
        Assert.Equal(new CodeSymbol("Parse", "function", 3, 9, "func Parse(s string) (UUID, error) {"), tags.Defs.Single());
        Assert.Equal(new CodeRef("UUID", 2), tags.Refs.Single());
        Assert.Null(tags.Error);
    }

    [Fact]
    public void Token_estimate_is_about_three_and_a_half_characters()
    {
        Assert.Equal(0, Tokens.Estimate(""));
        Assert.Equal(2, Tokens.Estimate("1234567"));
        Assert.Equal(3, Tokens.Estimate("12345678"));
    }
}
