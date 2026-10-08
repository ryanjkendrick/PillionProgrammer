using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Starts an agent session.</summary>
[DataContract]
public sealed class AgentSessionRequest
{
    /// <summary>Gets or sets the user's description of what the agent should do.</summary>
    [DataMember(Order = 0)]
    public string Goal { get; set; } = string.Empty;

    /// <summary>Gets or sets the model profile.</summary>
    [DataMember(Order = 1)]
    public Profile Profile { get; set; }

    /// <summary>Gets or sets an explicit model id, or <see langword="null"/> to choose by <see cref="Profile"/>.</summary>
    [DataMember(Order = 2)]
    public string? ModelId { get; set; }
}
