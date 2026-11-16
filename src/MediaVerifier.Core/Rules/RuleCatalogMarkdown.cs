using System.Globalization;
using System.Text;
using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Rules;

/// <summary>Generates docs/rules.md from the catalog (task M-07); a test keeps the committed file current.</summary>
public static class RuleCatalogMarkdown
{
    public static string Generate()
    {
        StringBuilder md = new();
        md.Append("# Rules\n\n");
        md.Append("Generated from `RuleCatalog` by the test suite (`RuleCatalogTests`); do not edit by hand. ");
        md.Append("Severity tiers and wording rules are in [subplan 02](../plans/02-findings-model.md).\n\n");

        foreach (FindingCategory category in Enum.GetValues<FindingCategory>())
        {
            List<RuleDefinition> rules = [.. RuleCatalog.All.Where(r => r.Category == category)];
            if (rules.Count == 0)
            {
                continue;
            }

            md.Append("## ").Append(category).Append("\n\n");
            md.Append("| ID | Title | Severity | Depth | Applies to | On by default | Milestone |\n");
            md.Append("|----|-------|----------|-------|------------|---------------|-----------|\n");
            foreach (RuleDefinition rule in rules)
            {
                md.Append("| `").Append(rule.Id).Append("` | ").Append(rule.Title)
                  .Append(" | ").Append(SeverityRange(rule))
                  .Append(" | ").Append(rule.OnVerification ? "verification" : rule.MinDepth.ToString())
                  .Append(" | ").Append(Kinds(rule.AppliesTo))
                  .Append(" | ").Append(rule.EnabledByDefault ? "yes" : "no")
                  .Append(" | ").Append(rule.Milestone == Milestone.Backlog ? "backlog" : rule.Milestone.ToString())
                  .Append(" |\n");
            }

            md.Append('\n');
            foreach (RuleDefinition rule in rules)
            {
                md.Append("### `").Append(rule.Id).Append("` ").Append(rule.Title).Append("\n\n");
                md.Append("- **What this means:** ").Append(rule.WhatItMeans).Append('\n');
                md.Append("- **Why it matters:** ").Append(rule.WhyItMatters).Append('\n');
                md.Append("- **What you can do:** ").Append(rule.WhatYouCanDo).Append('\n');
                md.Append("- **Actions:** ").Append(string.Join(", ", rule.ActionIds.Select(a => "`" + a + "`"))).Append('\n');
                List<RuleParameter> parameters = [.. RuleDefaults.ForRule(rule.Id)];
                if (parameters.Count > 0)
                {
                    md.Append("- **Limits:** ");
                    md.Append(string.Join("; ", parameters.Select(p => string.Create(
                        CultureInfo.InvariantCulture,
                        $"`{p.Name}` {p.Default} {p.Unit} ({p.Min}–{p.Max})"))));
                    md.Append('\n');
                }

                md.Append('\n');
            }
        }

        md.Append("## Run alerts\n\n");
        md.Append("| ID | Title | Severity | Trigger |\n");
        md.Append("|----|-------|----------|---------|\n");
        foreach (RunAlertDefinition alert in RuleCatalog.RunAlerts)
        {
            md.Append("| `").Append(alert.Id).Append("` | ").Append(alert.Title).Append(" | ").Append(alert.Severity).Append(" | ").Append(alert.Trigger).Append(" |\n");
        }

        return md.ToString();
    }

    private static string SeverityRange(RuleDefinition rule) =>
        rule.MinSeverity == rule.MaxSeverity ? rule.MaxSeverity.ToString() : rule.MinSeverity + " – " + rule.MaxSeverity;

    private static string Kinds(MediaKindMask mask)
    {
        if (mask == MediaKindMask.All)
        {
            return "all";
        }

        List<string> kinds = [];
        if (mask.HasFlag(MediaKindMask.Photo))
        {
            kinds.Add("photo");
        }

        if (mask.HasFlag(MediaKindMask.Video))
        {
            kinds.Add("video");
        }

        if (mask.HasFlag(MediaKindMask.Audio))
        {
            kinds.Add("audio");
        }

        return string.Join(", ", kinds);
    }
}
