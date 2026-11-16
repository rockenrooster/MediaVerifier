using System.Text.Json;
using System.Text.Json.Serialization;
using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Rules;

/// <summary>The user's changes to one rule: on/off, a severity cap and parameter overrides.</summary>
public sealed class RuleOverride
{
    public bool? Enabled { get; set; }

    public Severity? SeverityCap { get; set; }

    public Dictionary<string, double> Parameters { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public bool IsEmpty => Enabled is null && SeverityCap is null && Parameters.Count == 0;
}

/// <summary>
/// User overrides layered over the catalog defaults (task M-04). Applied at evaluation time, stored
/// separately from the defaults (subplan 03 persists the JSON), never changes the catalog itself.
/// </summary>
public sealed class RuleSettings
{
    private readonly Dictionary<string, RuleOverride> _overrides;

    public RuleSettings()
        : this(new Dictionary<string, RuleOverride>(StringComparer.Ordinal))
    {
    }

    private RuleSettings(Dictionary<string, RuleOverride> overrides)
    {
        _overrides = overrides;
    }

    /// <summary>Defaults only.</summary>
    public static RuleSettings Defaults { get; } = new();

    /// <summary>Raised after any change, so open findings can be re-evaluated without a rescan.</summary>
    public event EventHandler<string>? Changed;

    public IReadOnlyDictionary<string, RuleOverride> Overrides => _overrides;

    public bool IsEnabled(RuleDefinition rule) =>
        _overrides.TryGetValue(rule.Id, out RuleOverride? o) && o.Enabled is bool enabled ? enabled : rule.EnabledByDefault;

    /// <summary>The highest tier the rule may produce: the user's cap, never above the rule's own maximum.</summary>
    public Severity Cap(RuleDefinition rule)
    {
        Severity cap = rule.MaxSeverity;
        if (_overrides.TryGetValue(rule.Id, out RuleOverride? o) && o.SeverityCap is Severity user && user < cap)
        {
            cap = user < rule.MinSeverity ? rule.MinSeverity : user;
        }

        return cap;
    }

    public double Value(string ruleId, string name)
    {
        RuleParameter parameter = RuleDefaults.Get(ruleId, name);
        double? overrideValue = _overrides.TryGetValue(ruleId, out RuleOverride? o) && o.Parameters.TryGetValue(name, out double v) ? v : null;
        return RuleDefaults.Resolve(parameter, overrideValue);
    }

    public bool IsOverridden(string ruleId, string name) =>
        _overrides.TryGetValue(ruleId, out RuleOverride? o) && o.Parameters.ContainsKey(name);

    public void SetEnabled(string ruleId, bool enabled)
    {
        RuleDefinition rule = RuleCatalog.Get(ruleId);
        Edit(ruleId, o => o.Enabled = enabled == rule.EnabledByDefault ? null : enabled);
    }

    public void SetSeverityCap(string ruleId, Severity? cap)
    {
        RuleDefinition rule = RuleCatalog.Get(ruleId);
        Edit(ruleId, o => o.SeverityCap = cap is null || cap >= rule.MaxSeverity ? null : cap);
    }

    public void SetParameter(string ruleId, string name, double? value)
    {
        RuleParameter parameter = RuleDefaults.Get(ruleId, name);
        Edit(ruleId, o =>
        {
            if (value is null || parameter.Clamp(value.Value) == parameter.Default)
            {
                o.Parameters.Remove(name);
            }
            else
            {
                o.Parameters[name] = parameter.Clamp(value.Value);
            }
        });
    }

    /// <summary>
    /// Applies the overlay to a detector's raw finding: null when the rule is off; otherwise the severity
    /// is capped by the user, by the rule's range, and to Low for a heuristic below 0.5 confidence.
    /// </summary>
    public Finding? Apply(Finding raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        RuleDefinition rule = RuleCatalog.Get(raw.RuleId);
        if (!IsEnabled(rule))
        {
            return null;
        }

        Severity severity = SeverityPolicy.ClampToRule(raw.Severity, rule);
        Severity cap = Cap(rule);
        if (severity > cap)
        {
            severity = cap;
        }

        severity = SeverityPolicy.CapForConfidence(severity, raw.Confidence);
        return severity == raw.Severity ? raw : raw with { Severity = severity };
    }

    public string ToJson()
    {
        Dictionary<string, RuleOverride> kept = _overrides.Where(p => !p.Value.IsEmpty).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(kept, RuleSettingsJsonContext.Default.DictionaryStringRuleOverride);
    }

    /// <summary>Reads stored overrides, dropping unknown rules and parameters (a newer or older version wrote them).</summary>
    public static RuleSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RuleSettings();
        }

        Dictionary<string, RuleOverride>? stored;
        try
        {
            stored = JsonSerializer.Deserialize(json, RuleSettingsJsonContext.Default.DictionaryStringRuleOverride);
        }
        catch (JsonException)
        {
            return new RuleSettings();
        }

        return FromOverrides(stored ?? []);
    }

    /// <summary>Builds settings from stored overrides, dropping unknown rules and parameters and clamping values.</summary>
    public static RuleSettings FromOverrides(IReadOnlyDictionary<string, RuleOverride> stored)
    {
        Dictionary<string, RuleOverride> clean = new(StringComparer.Ordinal);
        foreach ((string ruleId, RuleOverride value) in stored)
        {
            if (!RuleCatalog.TryGet(ruleId, out _))
            {
                continue;
            }

            RuleOverride copy = new() { Enabled = value.Enabled, SeverityCap = value.SeverityCap };
            foreach ((string name, double parameterValue) in value.Parameters ?? [])
            {
                if (RuleDefaults.TryGet(ruleId + "." + name, out RuleParameter parameter))
                {
                    copy.Parameters[name] = parameter.Clamp(parameterValue);
                }
            }

            if (!copy.IsEmpty)
            {
                clean[ruleId] = copy;
            }
        }

        return new RuleSettings(clean);
    }

    private void Edit(string ruleId, Action<RuleOverride> edit)
    {
        if (!_overrides.TryGetValue(ruleId, out RuleOverride? o))
        {
            o = new RuleOverride();
            _overrides[ruleId] = o;
        }

        edit(o);
        if (o.IsEmpty)
        {
            _overrides.Remove(ruleId);
        }

        Changed?.Invoke(this, ruleId);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, RuleOverride>))]
internal sealed partial class RuleSettingsJsonContext : JsonSerializerContext;
