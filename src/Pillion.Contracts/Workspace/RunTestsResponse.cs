using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The outcome of a test run.</summary>
[DataContract]
public sealed class RunTestsResponse
{
    /// <summary>Gets or sets the number of tests that passed.</summary>
    [DataMember(Order = 0)]
    public int Passed { get; set; }

    /// <summary>Gets or sets the number of tests that failed.</summary>
    [DataMember(Order = 1)]
    public int Failed { get; set; }

    /// <summary>Gets or sets the number of tests that were skipped.</summary>
    [DataMember(Order = 2)]
    public int Skipped { get; set; }

    /// <summary>Gets or sets the names of the tests that failed.</summary>
    [DataMember(Order = 3)]
    public IReadOnlyList<string> FailedTests { get; set; } = Array.Empty<string>();
}
