namespace Pillion.Contracts;

/// <summary>
/// The mention kinds (design section 7, Context pinning with @-mentions): the values of <see cref="MentionRequest.Kind"/>,
/// <see cref="MentionRequest.Qualifier"/>, <see cref="MentionResolution.Kind"/> and <see cref="ConversationPin.Kind"/>. A kind
/// is a lower-case string on the wire, so these constants add no wire format; a host that does not know a kind answers it as
/// unresolved with a reason.
/// </summary>
/// <remarks>
/// Contract 1.2 defined <see cref="File"/>, <see cref="Folder"/>, <see cref="Selection"/>, <see cref="Errors"/> and
/// <see cref="Clipboard"/>. Contract 1.3 added <see cref="Symbol"/>, <see cref="Docs"/>, <see cref="Solution"/>,
/// <see cref="Git"/>, <see cref="Output"/>, <see cref="Build"/> and <see cref="Tests"/> (work item 2.13).
/// </remarks>
public static class MentionKinds
{
    /// <summary>A file's contents.</summary>
    public const string File = "file";

    /// <summary>A folder outline with file summaries.</summary>
    public const string Folder = "folder";

    /// <summary>The editor selection (the client sends it as the payload).</summary>
    public const string Selection = "selection";

    /// <summary>The diagnostics (the client sends them as the payload).</summary>
    public const string Errors = "errors";

    /// <summary>The clipboard text (the client sends it as the payload).</summary>
    public const string Clipboard = "clipboard";

    /// <summary>A symbol's definition and a summary of its references (Roslyn through the VSIX for C# and VB, the symbol table otherwise). Added in 1.3.</summary>
    public const string Symbol = "symbol";

    /// <summary>Markdown and text documentation in the repository and in user-registered folders. Added in 1.3.</summary>
    public const string Docs = "docs";

    /// <summary>The repo map of the open solution. Added in 1.3.</summary>
    public const string Solution = "solution";

    /// <summary>The working-tree diff, the staged changes or the last N commits (<see cref="MentionRequest.Arguments"/> says which). Added in 1.3.</summary>
    public const string Git = "git";

    /// <summary>A pane of the Visual Studio Output window (Build, Debug or Tests; the client sends its text as the payload). Added in 1.3.</summary>
    public const string Output = "output";

    /// <summary>The last build's output (the client sends it as the payload). Added in 1.3.</summary>
    public const string Build = "build";

    /// <summary>The last failing tests with their stack traces (the client sends them as the payload). Added in 1.3.</summary>
    public const string Tests = "tests";
}
