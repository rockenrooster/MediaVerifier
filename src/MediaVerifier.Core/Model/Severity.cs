namespace MediaVerifier.Core.Model;

/// <summary>Severity tiers (subplan 02). Stored as an integer so that a higher number is always worse.</summary>
public enum Severity : byte
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}
