namespace Pillion.Contracts;

/// <summary>The permission tier of a tool call; higher tiers carry more risk and need stronger approval.</summary>
public enum PermissionTier
{
    /// <summary>Tier 0: lowest risk.</summary>
    T0,

    /// <summary>Tier 1.</summary>
    T1,

    /// <summary>Tier 2.</summary>
    T2,

    /// <summary>Tier 3: highest risk.</summary>
    T3,
}
