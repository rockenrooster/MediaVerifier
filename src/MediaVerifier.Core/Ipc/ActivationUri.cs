using System.Globalization;

namespace MediaVerifier.Core.Ipc;

public enum ActivationTarget
{
    Run,
    Finding,
    Issues,
}

/// <summary>
/// A parsed activation link (subplan 13, Activation URIs; task N-02). Exactly three forms are accepted:
/// <c>mediaverifier://run/&lt;id&gt;</c>, <c>mediaverifier://finding/&lt;id&gt;</c> and <c>mediaverifier://issues</c>.
/// Links are untrusted input: they select a view by a positive integer id and never name a path or an action.
/// </summary>
public sealed record ActivationUri(ActivationTarget Target, long Id)
{
    /// <summary>The link for a target; ids must be positive.</summary>
    public override string ToString() => Target switch
    {
        ActivationTarget.Run => ActivationUriScheme.Prefix + "run/" + Id.ToString(CultureInfo.InvariantCulture),
        ActivationTarget.Finding => ActivationUriScheme.Prefix + "finding/" + Id.ToString(CultureInfo.InvariantCulture),
        _ => ActivationUriScheme.Prefix + "issues",
    };

    /// <summary>Parses a link; null for anything that is not exactly one of the three forms.</summary>
    public static ActivationUri? Parse(string? text)
    {
        if (!ActivationUriScheme.IsActivationUri(text))
        {
            return null;
        }

        string rest = text![ActivationUriScheme.Prefix.Length..];
        if (rest.EndsWith('/'))
        {
            rest = rest[..^1];
        }

        if (rest.Equals("issues", StringComparison.OrdinalIgnoreCase))
        {
            return new ActivationUri(ActivationTarget.Issues, 0);
        }

        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == rest.Length - 1)
        {
            return null;
        }

        string kind = rest[..slash];
        string number = rest[(slash + 1)..];
        // Canonical decimal only: ASCII digits, no sign, no leading zero, at most 18 digits (always fits a long).
        if (number.Length > 18 || number[0] == '0' || !number.All(char.IsAsciiDigit) || !long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
        {
            return null;
        }

        return kind.ToLowerInvariant() switch
        {
            "run" => new ActivationUri(ActivationTarget.Run, id),
            "finding" => new ActivationUri(ActivationTarget.Finding, id),
            _ => null,
        };
    }
}
