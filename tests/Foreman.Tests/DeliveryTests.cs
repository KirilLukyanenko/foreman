using Foreman.Implement;

namespace Foreman.Tests;

public class DeliveryTests
{
    private static readonly Fact[] Facts =
    [
        new("F1", "task", "The task: bulk discount at exactly 10 items."),
        new("F2", "tests", "The project's own test suite after the change: 9 of 9 tests pass; 0 tests that passed before fail now."),
        new("F3", "default", "The client didn't answer \"Is 10 included?\"; the default was used: yes", MustMention: true),
        new("F4", "lint_not_run", "The project's check `mypy` couldn't run in the isolated environment.", MustMention: true),
    ];

    private static ReportDraft Draft(string open = "You didn't say whether 10 counts; I assumed it does. mypy couldn't run here, please run it.") => new(
        "Bulk discount fixed",
        new ReportItem("Orders of exactly 10 items now get the discount.", ["F1"]),
        [new ReportItem("The discount now starts at 10 items.", ["F1"])],
        [new ReportItem("All 9 tests of the project pass.", ["F2"])],
        [new ReportItem(open, ["F3", "F4"])],
        new ReportHeadings("Done", "Checked", "Good to know"));

    [Fact]
    public void A_report_that_rests_on_its_facts_passes() => Assert.Empty(DeliveryTools.Check(Draft(), Facts, "en"));

    [Fact]
    public void A_number_no_cited_fact_says_is_refused()
    {
        var d = Draft() with { Checked = [new ReportItem("All 12 tests pass.", ["F2"])] };
        Assert.Contains(DeliveryTools.Check(d, Facts, "en"), e => e.Contains("says 12", StringComparison.Ordinal));
        var stray = Draft() with { Done = [new ReportItem("The discount starts at 10 items.", ["F2"])] };
        Assert.Contains(DeliveryTools.Check(stray, Facts, "en"), e => e.Contains("says 10", StringComparison.Ordinal));
    }

    [Fact]
    public void Facts_the_client_must_hear_must_be_in_the_open_part()
    {
        var d = Draft() with { Open = [new ReportItem("You didn't say whether 10 counts; I assumed it does.", ["F3"])] };
        Assert.Contains(DeliveryTools.Check(d, Facts, "en"), e => e.StartsWith("F4 must be in the open part", StringComparison.Ordinal));
    }

    [Fact]
    public void Lines_without_facts_unknown_facts_and_internal_names_are_refused()
    {
        var d = Draft() with
        {
            Done = [new ReportItem("Implemented R1 from the PRP.", ["F1"]), new ReportItem("Also tidied the code.", []), new ReportItem("Faster now.", ["F9"])],
        };
        var errors = DeliveryTools.Check(d, Facts, "en");
        Assert.Contains(errors, e => e.Contains("names \"R1\"", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("cites no fact", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("cites F9, which doesn't exist", StringComparison.Ordinal));
    }

    [Fact]
    public void The_report_is_in_the_clients_language()
    {
        Assert.Contains(DeliveryTools.Check(Draft(), Facts, "ru"), e => e.Contains("must be in ru", StringComparison.Ordinal));
        var ru = new ReportDraft(
            "Скидка за объём исправлена",
            new ReportItem("Заказ ровно на 10 штук теперь получает скидку.", ["F1"]),
            [new ReportItem("Скидка начинается с 10 штук.", ["F1"])],
            [new ReportItem("Все 9 тестов проекта проходят.", ["F2"])],
            [new ReportItem("Вы не ответили, считать ли 10; я исходил из того, что да. Проверку `mypy` здесь запустить не удалось, запустите её у себя.", ["F3", "F4"])],
            new ReportHeadings("Что сделано", "Как проверено", "Что важно знать"));
        Assert.Empty(DeliveryTools.Check(ru, Facts, "ru"));
        Assert.Contains(DeliveryTools.Check(ru, Facts, "en"), e => e.Contains("must be in en", StringComparison.Ordinal));
    }

    [Fact]
    public void Numbers_split_on_anything_that_is_not_a_digit() =>
        Assert.Equal(["9", "0", "2", "808", "0"], DeliveryTools.Numbers("9,0 and 2 808 and 007x").Take(4).Append(DeliveryTools.Numbers("00").Single()));

    [Fact]
    public void The_markdown_leaves_out_empty_sections()
    {
        var md = DeliveryTools.Markdown(Draft() with { Open = [] });
        Assert.StartsWith("# Bulk discount fixed\n\nOrders of exactly 10 items", md);
        Assert.Contains("## Checked\n\n- All 9 tests of the project pass.\n", md);
        Assert.DoesNotContain("Good to know", md);
    }
}
