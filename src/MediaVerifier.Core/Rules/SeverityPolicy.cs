using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Rules;

/// <summary>Tier and wording rules from subplan 02, in one place.</summary>
public static class SeverityPolicy
{
    /// <summary>Confidence at or above which a finding is stated as fact.</summary>
    public const float FactConfidence = 1f;

    public static ConfidenceWording Wording(float confidence) => confidence switch
    {
        >= FactConfidence => ConfidenceWording.Fact,
        >= 0.8f => ConfidenceWording.Likely,
        >= 0.5f => ConfidenceWording.Possibly,
        _ => ConfidenceWording.Suspicious,
    };

    /// <summary>The confidence pill text in the detail drawer; null for facts.</summary>
    public static string? PillText(float confidence) => Wording(confidence) switch
    {
        ConfidenceWording.Likely => "Likely",
        ConfidenceWording.Possibly => "Possibly",
        ConfidenceWording.Suspicious => "Suspicious",
        _ => null,
    };

    /// <summary>
    /// Hedges a title or summary for its confidence: facts unchanged, then "Likely …", "Possibly …",
    /// "Suspicious: …".
    /// </summary>
    public static string Hedge(string text, float confidence)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Wording(confidence) switch
        {
            ConfidenceWording.Likely => "Likely " + LowerFirst(text),
            ConfidenceWording.Possibly => "Possibly " + LowerFirst(text),
            ConfidenceWording.Suspicious => "Suspicious: " + text,
            _ => text,
        };
    }

    /// <summary>A heuristic below 0.5 confidence is never above Low (subplan 02, rule 2).</summary>
    public static Severity CapForConfidence(Severity severity, float confidence) =>
        confidence < 0.5f && severity > Severity.Low ? Severity.Low : severity;

    /// <summary>Keeps a severity inside the rule's range; quality and authenticity rules never reach Critical.</summary>
    public static Severity ClampToRule(Severity severity, RuleDefinition rule)
    {
        if (severity < rule.MinSeverity)
        {
            severity = rule.MinSeverity;
        }

        if (severity > rule.MaxSeverity)
        {
            severity = rule.MaxSeverity;
        }

        if (rule.Category is FindingCategory.Quality or FindingCategory.Authenticity && severity > Severity.High)
        {
            severity = Severity.High;
        }

        return severity;
    }

    /// <summary>A file's status is the worst severity among its open findings.</summary>
    public static Severity Aggregate(IEnumerable<Severity> openSeverities)
    {
        Severity worst = Severity.None;
        foreach (Severity severity in openSeverities)
        {
            if (severity > worst)
            {
                worst = severity;
            }
        }

        return worst;
    }

    /// <summary>Maps a confidence onto a rule's range, for heuristics whose "severity follows confidence".</summary>
    public static Severity FromConfidence(RuleDefinition rule, float confidence)
    {
        Severity severity = Wording(confidence) switch
        {
            ConfidenceWording.Fact or ConfidenceWording.Likely => rule.MaxSeverity,
            ConfidenceWording.Possibly => (Severity)Math.Max((int)rule.MinSeverity, (int)rule.MaxSeverity - 1),
            _ => rule.MinSeverity,
        };
        return CapForConfidence(ClampToRule(severity, rule), confidence);
    }

    private static string LowerFirst(string text)
    {
        if (text.Length >= 2 && char.IsUpper(text[0]) && char.IsLower(text[1]))
        {
            return char.ToLowerInvariant(text[0]) + text[1..];
        }

        return text;
    }
}
