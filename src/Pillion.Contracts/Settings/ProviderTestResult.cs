using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The result of testing a model provider's connection and credentials.</summary>
[DataContract]
public sealed class ProviderTestResult
{
    /// <summary>Gets or sets the id of the provider that was tested.</summary>
    [DataMember(Order = 0)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the test succeeded.</summary>
    [DataMember(Order = 1)]
    public bool IsSuccess { get; set; }

    /// <summary>Gets or sets a user-facing description of the result.</summary>
    [DataMember(Order = 2)]
    public string? Message { get; set; }
}
