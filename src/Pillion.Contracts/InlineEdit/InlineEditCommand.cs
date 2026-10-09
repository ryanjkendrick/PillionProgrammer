namespace Pillion.Contracts;

/// <summary>The slash shortcut typed in the inline chat prompt (design section 5).</summary>
public enum InlineEditCommand
{
    /// <summary>No shortcut: the instruction says what to change.</summary>
    None,

    /// <summary><c>/doc</c>: add documentation comments only; the code itself does not change.</summary>
    Doc,

    /// <summary>
    /// <c>/test</c>: write a test method or class for the scope. The result is new code, not a replacement of the scope;
    /// <see cref="InlineEditChunk.Explanation"/> says where it belongs.
    /// </summary>
    Test,

    /// <summary><c>/fix</c>: fix the problems reported in <see cref="InlineEditRequest.Diagnostics"/>.</summary>
    Fix,

    /// <summary><c>/explain</c>: explain the scope in prose; there is no replacement.</summary>
    Explain,
}
