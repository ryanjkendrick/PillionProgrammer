using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Asks the host to resolve an <c>@</c> mention typed in chat.</summary>
[DataContract]
public sealed class MentionRequest
{
    /// <summary>Gets or sets the mention text, for example <c>@file:Program.cs</c>.</summary>
    [DataMember(Order = 0)]
    public string Mention { get; set; } = string.Empty;
}
