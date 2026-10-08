namespace Pillion.Contracts;

/// <summary>A user-facing model profile that trades speed, cost and quality.</summary>
public enum Profile
{
    /// <summary>The fastest available cloud model.</summary>
    Fast,

    /// <summary>A balance of speed and quality.</summary>
    Balanced,

    /// <summary>The most capable available cloud model.</summary>
    Frontier,

    /// <summary>A fast model running on this machine.</summary>
    LocalFast,

    /// <summary>The most capable model running on this machine.</summary>
    LocalStrong,

    /// <summary>Let Pillion choose based on the task, connectivity and settings.</summary>
    Auto,
}
