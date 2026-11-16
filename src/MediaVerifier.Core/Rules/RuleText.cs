using System.Globalization;
using System.Text;

namespace MediaVerifier.Core.Rules;

/// <summary>
/// Fills <c>{key}</c> placeholders in rule explanations from a finding's evidence. A placeholder with
/// no evidence value uses its default, so the text always reads as a sentence.
/// </summary>
public static class RuleText
{
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["sourceName"] = "lossy",
        ["sourcePhrase"] = "a lossy file",
        ["formatName"] = "a lossless format",
    };

    public static string Render(string template, IReadOnlyDictionary<string, object>? evidence)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!template.Contains('{', StringComparison.Ordinal))
        {
            return template;
        }

        StringBuilder text = new(template.Length + 16);
        int index = 0;
        while (index < template.Length)
        {
            int open = template.IndexOf('{', index);
            if (open < 0)
            {
                text.Append(template, index, template.Length - index);
                break;
            }

            int close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                text.Append(template, index, template.Length - index);
                break;
            }

            text.Append(template, index, open - index);
            string key = template[(open + 1)..close];
            text.Append(Resolve(key, evidence));
            index = close + 1;
        }

        return text.ToString();
    }

    /// <summary>The placeholder keys a template uses.</summary>
    public static IReadOnlyList<string> Placeholders(string template)
    {
        List<string> keys = [];
        int index = 0;
        while ((index = template.IndexOf('{', index)) >= 0)
        {
            int close = template.IndexOf('}', index + 1);
            if (close < 0)
            {
                break;
            }

            keys.Add(template[(index + 1)..close]);
            index = close + 1;
        }

        return keys;
    }

    public static bool HasDefault(string key) => Defaults.ContainsKey(key);

    private static string Resolve(string key, IReadOnlyDictionary<string, object>? evidence)
    {
        if (evidence is not null && evidence.TryGetValue(key, out object? value) && value is not null)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        return Defaults.TryGetValue(key, out string? fallback) ? fallback : "";
    }
}
