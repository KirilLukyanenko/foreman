using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Foreman.Recon.Reports;

public enum TestStatus { Passed, Failed, Skipped }

/// <summary>One test in one run. The id is stable across runs: it is how pass-to-pass is computed later.</summary>
public sealed record TestOutcome(string Id, TestStatus Status, double? Seconds);

/// <summary>
/// Turns the machine-readable reports of .NET (TRX), JUnit XML producers (pytest, Vitest, Mocha, node:test,
/// jest-junit) and Jest (JSON) into one list of outcomes. Unknown files are ignored.
/// </summary>
public static class TestReports
{
    private static readonly XmlReaderSettings SafeXml = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    public static IReadOnlyList<TestOutcome> ParseFolder(string dir, string? repoRoot = null)
    {
        var all = new List<TestOutcome>();
        if (!Directory.Exists(dir)) return all;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            try
            {
                if (ext == ".trx") all.AddRange(ParseTrx(File.ReadAllText(file)));
                else if (ext == ".xml") all.AddRange(ParseJUnit(File.ReadAllText(file)));
                else if (ext == ".json" && Path.GetFileName(file).StartsWith("jest", StringComparison.OrdinalIgnoreCase))
                    all.AddRange(ParseJestJson(File.ReadAllText(file), repoRoot));
            }
            catch (Exception e) when (e is XmlException or JsonException or InvalidOperationException)
            {
                // A truncated report (killed process) must not hide the others.
            }
        }
        return Deduplicate(all);
    }

    /// <summary>Visual Studio test results: the format of <c>dotnet test --logger trx</c> and MTP's <c>--report-trx</c>.</summary>
    public static IEnumerable<TestOutcome> ParseTrx(string xml)
    {
        var doc = Load(xml);
        XNamespace ns = doc.Root!.Name.Namespace;
        var definitions = doc.Descendants(ns + "UnitTest").ToDictionary(
            u => (string?)u.Attribute("id") ?? "",
            u => Path.GetFileNameWithoutExtension((string?)u.Attribute("storage") ?? ""));

        foreach (var r in doc.Descendants(ns + "UnitTestResult"))
        {
            var name = (string?)r.Attribute("testName") ?? "";
            var assembly = definitions.GetValueOrDefault((string?)r.Attribute("testId") ?? "") ?? "";
            var status = ((string?)r.Attribute("outcome"))?.ToLowerInvariant() switch
            {
                "passed" => TestStatus.Passed,
                "notexecuted" or "inconclusive" or "pending" or "disconnected" => TestStatus.Skipped,
                _ => TestStatus.Failed,
            };
            double? seconds = TimeSpan.TryParse((string?)r.Attribute("duration"), CultureInfo.InvariantCulture, out var d) ? d.TotalSeconds : null;
            yield return new TestOutcome(assembly.Length > 0 ? $"{assembly}::{name}" : name, status, seconds);
        }
    }

    /// <summary>JUnit XML: <c>testsuites/testsuite/testcase</c>, failure, error and skipped children.</summary>
    public static IEnumerable<TestOutcome> ParseJUnit(string xml)
    {
        var doc = Load(xml);
        foreach (var tc in doc.Descendants().Where(e => e.Name.LocalName == "testcase"))
        {
            var cls = (string?)tc.Attribute("classname") ?? (string?)tc.Attribute("file") ?? (string?)tc.Parent?.Attribute("name") ?? "";
            var name = (string?)tc.Attribute("name") ?? "";
            var children = tc.Elements().Select(e => e.Name.LocalName).ToHashSet();
            var status =
                children.Contains("failure") || children.Contains("error") ? TestStatus.Failed :
                children.Contains("skipped") ? TestStatus.Skipped : TestStatus.Passed;
            double? seconds = double.TryParse((string?)tc.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : null;
            yield return new TestOutcome(cls.Length > 0 ? $"{cls}::{name}" : name, status, seconds);
        }
    }

    /// <summary>Jest's <c>--json --outputFile</c> report.</summary>
    public static IEnumerable<TestOutcome> ParseJestJson(string json, string? repoRoot)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("testResults", out var files)) yield break;
        foreach (var file in files.EnumerateArray())
        {
            var path = file.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (repoRoot is not null && path.StartsWith(repoRoot, StringComparison.Ordinal)) path = path[repoRoot.Length..].TrimStart('/');
            if (!file.TryGetProperty("assertionResults", out var tests)) continue;
            foreach (var t in tests.EnumerateArray())
            {
                var name = t.TryGetProperty("fullName", out var fn) ? fn.GetString() : t.TryGetProperty("title", out var ti) ? ti.GetString() : "";
                var status = (t.TryGetProperty("status", out var s) ? s.GetString() : "") switch
                {
                    "passed" => TestStatus.Passed,
                    "pending" or "skipped" or "todo" or "disabled" => TestStatus.Skipped,
                    _ => TestStatus.Failed,
                };
                double? seconds = t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() / 1000 : null;
                yield return new TestOutcome($"{path}::{name}", status, seconds);
            }
        }
    }

    /// <summary>
    /// Parameterised tests can repeat a name. Repeats get a #2, #3 suffix in report order, so ids stay unique and
    /// stable as long as the order is.
    /// </summary>
    internal static IReadOnlyList<TestOutcome> Deduplicate(IEnumerable<TestOutcome> outcomes)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<TestOutcome>();
        foreach (var o in outcomes)
        {
            var n = seen[o.Id] = seen.GetValueOrDefault(o.Id) + 1;
            result.Add(n == 1 ? o : o with { Id = $"{o.Id}#{n}" });
        }
        return result;
    }

    private static XDocument Load(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), SafeXml);
        return XDocument.Load(reader);
    }
}
