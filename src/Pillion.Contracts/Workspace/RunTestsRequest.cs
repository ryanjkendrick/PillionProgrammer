using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Runs tests in the solution.</summary>
[DataContract]
public sealed class RunTestsRequest
{
    /// <summary>Gets or sets a test filter expression, or <see langword="null"/> to run all tests.</summary>
    [DataMember(Order = 0)]
    public string? Filter { get; set; }
}
