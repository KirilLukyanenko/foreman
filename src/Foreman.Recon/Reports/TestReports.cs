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
    /// Makes ids stable across runs. Parameterised tests often put run-time values in their display names
    /// (<c>DateTime.Now</c>, <c>Guid.NewGuid()</c>), so the same test gets a new name every run; those values become
    /// placeholders. Names that then repeat get a #2, #3 suffix in report order.
    /// </summary>
    internal static IReadOnlyList<TestOutcome> Deduplicate(IEnumerable<TestOutcome> outcomes)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<TestOutcome>();
        foreach (var o in outcomes)
        {
            var id = NormalizeId(o.Id);
            var n = seen[id] = seen.GetValueOrDefault(id) + 1;
            result.Add(o with { Id = n == 1 ? id : $"{id}#{n}" });
        }
        return result;
    }

    internal static string NormalizeId(string id)
    {
        id = VolatileGuid.Replace(id, "<guid>");
        id = VolatileIsoTime.Replace(id, "<time>");
        id = VolatileLocalTime.Replace(id, "<time>");
        return VolatileAddress.Replace(id, "<addr>");
    }

    private static readonly System.Text.RegularExpressions.Regex VolatileGuid =
        new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex VolatileIsoTime =
        new(@"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex VolatileLocalTime =
        new(@"\b\d{1,2}/\d{1,2}/\d{4} \d{1,2}:\d{2}:\d{2}( ?[AP]M)?( [+-]\d{2}:\d{2})?", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex VolatileAddress =
        new(@"\b0x[0-9a-fA-F]{6,}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static XDocument Load(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), SafeXml);
        return XDocument.Load(reader);
    }
}
