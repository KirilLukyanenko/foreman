using System.Text.Json;
using Foreman.Core;
using Foreman.Evals;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Dossier;

namespace Foreman.Tests;

public sealed class DossierTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "foreman-tests", Guid.NewGuid().ToString("N"));
    private readonly Job _job;

    public DossierTests()
    {
        _job = Job.Create(_home, "https://example.invalid/repo.git", null);
        Write("src/orders.py", "class Order:\n    def total(self):\n        return 1\n\n\ndef helper():\n    pass\n");
        Write("tests/test_orders.py", "from src.orders import Order\n\ndef test_total():\n    assert Order().total() == 1\n");
        Write("node_modules/x/index.js", "module.exports = 1\n");
        _job.WriteJson("codemap", "index.json", new CodeIndex([
            new FileTags("src/orders.py", "python", 8, [new CodeSymbol("Order", "class", 1, 3, "class Order:"), new CodeSymbol("total", "function", 2, 3, "    def total(self):"), new CodeSymbol("helper", "function", 6, 7, "def helper():")], []),
            new FileTags("tests/test_orders.py", "python", 5, [new CodeSymbol("test_total", "function", 3, 4, "def test_total():")], [new CodeRef("Order", 2), new CodeRef("total", 1)]),
        ]));
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(_job.SourceDir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static Dictionary<string, JsonElement> Input(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    [Fact]
    public async Task Read_tools_stay_inside_the_repository()
    {
        await using var tools = new ReaderTools(_job, "architecture");
        await tools.StartAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        var read = await tools.CallAsync("read_file", Input(new { path = "./src/orders.py", start_line = 2, end_line = 3 }), ct);
        Assert.False(read.IsError);
        Assert.Contains("    2│    def total(self):", read.Text);

        Assert.True((await tools.CallAsync("read_file", Input(new { path = "../../job.json" }), ct)).IsError);
        Assert.True((await tools.CallAsync("read_file", Input(new { path = "node_modules/x/index.js" }), ct)).IsError);

        var list = await tools.CallAsync("list_files", Input(new { glob = "*.py" }), ct);
        Assert.Contains("src/orders.py", list.Text);
        Assert.DoesNotContain("node_modules", list.Text);

        var search = await tools.CallAsync("search", Input(new { pattern = "def \\w+\\(", regex = true }), ct);
        Assert.Contains("src/orders.py:6: def helper():", search.Text);

        var symbol = await tools.CallAsync("find_symbol", Input(new { name = "Order" }), ct);
        Assert.Contains("src/orders.py:1", symbol.Text);
        Assert.Contains("tests/test_orders.py (2)", symbol.Text);

        var bad = await tools.CallAsync("search", Input(new { pattern = "(", regex = true }), ct);
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Submit_checks_every_reference_before_accepting()
    {
        await using var tools = new ReaderTools(_job, "task");
        await tools.StartAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        var wrong = await tools.CallAsync("submit", Input(new
        {
            markdown = "The total is computed in `src/orders.py:2` and checked in tests/test_orders.py:99.",
            citations = new[] { new { path = "src/orders.py", line = 2 } },
            locations = new[] { new { path = "src/orders.py", symbols = new[] { "Order.subtotal" }, reason = "computes it", confidence = "high" } },
        }), ct);
        Assert.True(wrong.IsError);
        Assert.Contains("tests/test_orders.py:99", wrong.Text);
        Assert.Contains("'Order.subtotal' is not defined there", wrong.Text);
        Assert.False(tools.Finished);

        var right = await tools.CallAsync("submit", Input(new
        {
            markdown = "The total is computed in `src/orders.py:2` and checked in tests/test_orders.py:3.",
            citations = new[] { new { path = "src/orders.py", line = 2, note = "Order.total" } },
            locations = new[] { new { path = "src/orders.py", symbols = new[] { "Order.total" }, reason = "computes it", confidence = "high" } },
            open_questions = new[] { "Should discounts apply before tax? Assume yes." },
        }), ct);
        Assert.False(right.IsError);
        Assert.True(tools.Finished);
        Assert.Equal("submitted", tools.Outcome);
        var saved = Json.Read<DossierSection>(File.ReadAllText(tools.ResultFile))!;
        Assert.Equal("src/orders.py", saved.Locations!.Single().Path);
        Assert.Empty(saved.Warnings);
        Assert.Equal(2, saved.Submissions);
    }

    [Fact]
    public async Task The_third_failed_submission_is_kept_with_warnings()
    {
        await using var tools = new ReaderTools(_job, "risks");
        await tools.StartAsync(TestContext.Current.CancellationToken);
        var input = Input(new { markdown = "Risky.", citations = new[] { new { path = "nope.py", line = 1 } } });
        for (var i = 0; i < ReaderTools.MaxSubmissions - 1; i++)
            Assert.True((await tools.CallAsync("submit", input, TestContext.Current.CancellationToken)).IsError);
        Assert.False((await tools.CallAsync("submit", input, TestContext.Current.CancellationToken)).IsError);
        Assert.NotEmpty(Json.Read<DossierSection>(File.ReadAllText(tools.ResultFile))!.Warnings);
    }

    [Theory]
    [InlineData("./src/a.cs", "src/a.cs")]
    [InlineData("/src/../../etc/passwd", "src/etc/passwd")]
    [InlineData("`src\\a.cs`", "src/a.cs")]
    public void Paths_are_cleaned_into_the_repository(string input, string expected) => Assert.Equal(expected, ReaderTools.Clean(input));

    [Fact]
    public void Prompts_are_assembled_per_section()
    {
        foreach (var section in ReaderTools.Sections)
        {
            var prompt = DossierStage.SystemPrompt(section, hasTask: true);
            Assert.Contains($"Your section: **{section}**", prompt.Text);
            Assert.DoesNotContain("{{", prompt.Text);
        }
    }

    [Fact]
    public void Editorconfig_settings_for_the_main_language()
    {
        var settings = RulesFile.EditorConfig("""
            root = true
            [*]
            indent_style = space
            charset = utf-8
            [*.cs]
            indent_size = 4
            dotnet_style_qualification_for_field = false
            [*.json]
            indent_size = 2
            """, "cs");
        Assert.Equal(new Dictionary<string, string> { ["indent_style"] = "space", ["charset"] = "utf-8", ["indent_size"] = "4" }, settings);
    }

    [Fact]
    public void Reciprocal_rank_fusion_rewards_agreement()
    {
        var a = new[] { new RankedFile("x", 3), new RankedFile("y", 2), new RankedFile("z", 1) };
        var b = new[] { new RankedFile("y", 9), new RankedFile("z", 8), new RankedFile("x", 7) };
        Assert.Equal("y", LocalizationEval.Fuse(a, b)[0].Path);
    }

    [Fact]
    public void Many_known_red_tests_are_summarised_by_where_they_are()
    {
        var sb = new System.Text.StringBuilder();
        var names = Enumerable.Range(1, 40).Select(i => $"app.integrationtests::App.IntegrationTests.Api.Test{i}").ToList();
        RulesFile.AppendTests(sb, "Known-red", names, 40);
        Assert.Equal("  - Known-red: 40, all in `app.integrationtests`; full list in `baseline/`\n", sb.ToString());

        sb.Clear();
        RulesFile.AppendTests(sb, "Flaky", ["a.b.C.one", "a.b.C.two"], 2);
        Assert.Equal("  - Flaky: 2: `a.b.C.one`; `a.b.C.two`\n", sb.ToString());
    }

    public void Dispose()
    {
        try { Dirs.Delete(_home); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
