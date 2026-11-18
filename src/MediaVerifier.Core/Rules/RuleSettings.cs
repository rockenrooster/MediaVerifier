using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Rules;

/// <summary>The user's changes to one rule: on/off, a severity cap and parameter overrides.</summary>
public sealed class RuleOverride
{
    public bool? Enabled { get; set; }

    public Severity? SeverityCap { get; set; }

    public Dictionary<string, double> Parameters { get; set; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Enabled is null && SeverityCap is null && Parameters.Count == 0;

    internal RuleOverride Copy() => new() { Enabled = Enabled, SeverityCap = SeverityCap, Parameters = new Dictionary<string, double>(Parameters, StringComparer.Ordinal) };
}

/// <summary>
/// User overrides layered over the catalog defaults (task M-04). Applied at evaluation time, stored
/// separately from the defaults (subplan 03 persists them as rows), never changes the catalog itself.
/// Settings → Rules edits the overrides on the UI thread while scan workers and re-evaluation read them, so every
/// access takes the lock (uncontended, a few dozen entries).
/// </summary>
public sealed class RuleSettings
{
    private readonly Dictionary<string, RuleOverride> _overrides;
    private readonly Lock _gate = new();

    public RuleSettings()
        : this(new Dictionary<string, RuleOverride>(StringComparer.Ordinal))
    {
    }

    private RuleSettings(Dictionary<string, RuleOverride> overrides)
    {
        _overrides = overrides;
    }

    /// <summary>Defaults only: a new instance each time, so an edit never reaches anyone else's defaults.</summary>
    public static RuleSettings Defaults => new();

    /// <summary>A copy of the overrides as they are now (the store saves it).</summary>
    public IReadOnlyDictionary<string, RuleOverride> Overrides
    {
        get
        {
            lock (_gate)
            {
                return _overrides.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal);
            }
        }
    }

    public bool IsEnabled(RuleDefinition rule)
    {
        lock (_gate)
        {
            return _overrides.TryGetValue(rule.Id, out RuleOverride? o) && o.Enabled is bool enabled ? enabled : rule.EnabledByDefault;
        }
    }

    /// <summary>The highest tier the rule may produce: the user's cap, never above the rule's own maximum.</summary>
    public Severity Cap(RuleDefinition rule)
    {
        Severity cap = rule.MaxSeverity;
        lock (_gate)
        {
            if (_overrides.TryGetValue(rule.Id, out RuleOverride? o) && o.SeverityCap is Severity user && user < cap)
            {
                cap = user < rule.MinSeverity ? rule.MinSeverity : user;
            }
        }

        return cap;
    }

    public double Value(string ruleId, string name)
    {
        RuleParameter parameter = RuleDefaults.Get(ruleId, name);
        double? overrideValue;
        lock (_gate)
        {
            overrideValue = _overrides.TryGetValue(ruleId, out RuleOverride? o) && o.Parameters.TryGetValue(name, out double v) ? v : null;
        }

        return RuleDefaults.Resolve(parameter, overrideValue);
    }

    public bool IsOverridden(string ruleId, string name)
    {
        lock (_gate)
        {
            return _overrides.TryGetValue(ruleId, out RuleOverride? o) && o.Parameters.ContainsKey(name);
        }
    }

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

    /// <summary>A copy of one rule's override, or null when it has none (to put it back with <see cref="Restore"/>).</summary>
    public RuleOverride? Snapshot(string ruleId)
    {
        lock (_gate)
        {
            return _overrides.TryGetValue(ruleId, out RuleOverride? o) ? o.Copy() : null;
        }
    }

    /// <summary>Puts back one rule's override as <see cref="Snapshot"/> took it (a change that could not be saved).</summary>
    public void Restore(string ruleId, RuleOverride? snapshot)
    {
        lock (_gate)
        {
            if (snapshot is null || snapshot.IsEmpty)
            {
                _overrides.Remove(ruleId);
            }
            else
            {
                _overrides[ruleId] = snapshot.Copy();
            }
        }
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
        lock (_gate)
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
        }
    }
}
