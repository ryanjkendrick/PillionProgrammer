namespace Pillion.ContextEngine.Chunking;

/// <summary>The kind of unit a <see cref="Chunk"/> holds.</summary>
public enum ChunkKind
{
    /// <summary>
    /// File-level code outside any type or member: using and import directives, package clauses, top-level statements
    /// and script code.
    /// </summary>
    Document,

    /// <summary>
    /// A type's declaration without its members: attributes, modifiers, name, base list and constraints, plus any
    /// leading content of the body before the first member (a docstring, for example). A type with no members, such
    /// as an enum or a struct of fields, is a type header as a whole.
    /// </summary>
    TypeHeader,

    /// <summary>A member (method, property, function, field group, CSS rule...) with its doc comment and attributes.</summary>
    Member,

    /// <summary>A SQL statement or batch.</summary>
    Statement,

    /// <summary>Markup: an HTML element or a group of sibling elements, or the markup of a Razor file.</summary>
    Markup,

    /// <summary>A fixed window of lines from the fallback chunker; windows overlap.</summary>
    Window,
}
