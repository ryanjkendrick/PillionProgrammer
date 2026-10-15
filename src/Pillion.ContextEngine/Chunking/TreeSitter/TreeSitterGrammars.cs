using static Pillion.ContextEngine.Chunking.TreeSitter.TreeSitterGrammar;

namespace Pillion.ContextEngine.Chunking.TreeSitter;

/// <summary>
/// The grammar descriptions Pillion ships, for the grammars bundled by TreeSitter.DotNet 1.3.0. HTML is listed for
/// completeness of the data but is chunked by its own markup walker (<see cref="HtmlChunker"/>).
/// </summary>
public static class TreeSitterGrammars
{
    private static readonly IReadOnlySet<string> s_jsLeading = Set("comment", "decorator", "hash_bang_line");
    private static readonly IReadOnlySet<string> s_jsFunctionValues = Set("arrow_function", "function_expression", "function", "generator_function");

    /// <summary>JavaScript (and JSX, which the grammar includes).</summary>
    public static TreeSitterGrammar JavaScript { get; } = new()
    {
        Language = LanguageIds.JavaScript,
        GrammarId = "JavaScript",
        Types = Set("class_declaration"),
        Members = Set("function_declaration", "generator_function_declaration", "method_definition", "class_static_block"),
        FieldMembers = Set("field_definition"),
        Wrappers = Set("export_statement"),
        Leading = s_jsLeading,
        Blocks = Set("statement_block", "switch_body", "class_body", "object"),
        BodyTypes = Set("class_body"),
        FunctionValueDeclarations = Set("lexical_declaration", "variable_declaration"),
        FunctionValueTypes = s_jsFunctionValues,
        NameFields = ["name", "property"],
    };

    /// <summary>TypeScript.</summary>
    public static TreeSitterGrammar TypeScript { get; } = new()
    {
        Language = LanguageIds.TypeScript,
        GrammarId = "TypeScript",
        Namespaces = Set("internal_module", "module"),
        Types = Set("class_declaration", "abstract_class_declaration", "interface_declaration", "enum_declaration"),
        Members = Set("function_declaration", "generator_function_declaration", "function_signature", "method_definition",
            "method_signature", "abstract_method_signature", "class_static_block", "type_alias_declaration",
            "call_signature", "construct_signature"),
        FieldMembers = Set("public_field_definition", "field_definition", "property_signature", "index_signature"),
        Wrappers = Set("export_statement", "ambient_declaration", "expression_statement"),
        Leading = s_jsLeading,
        Blocks = Set("statement_block", "switch_body", "class_body", "object", "interface_body", "object_type"),
        BodyTypes = Set("class_body", "interface_body", "object_type", "enum_body", "statement_block"),
        FunctionValueDeclarations = Set("lexical_declaration", "variable_declaration"),
        FunctionValueTypes = s_jsFunctionValues,
    };

    /// <summary>TypeScript with JSX (<c>.tsx</c>); the same node types as <see cref="TypeScript"/>.</summary>
    public static TreeSitterGrammar Tsx { get; } = TypeScript with { Language = LanguageIds.Tsx, GrammarId = "TSX" };

    /// <summary>Python.</summary>
    public static TreeSitterGrammar Python { get; } = new()
    {
        Language = LanguageIds.Python,
        GrammarId = "Python",
        Types = Set("class_definition"),
        Members = Set("function_definition"),
        Wrappers = Set("decorated_definition"),
        Leading = Set("comment"),
        Blocks = Set("block"),
    };

    private static readonly IReadOnlySet<string> s_cLeading = Set("comment", "identifier");
    private static readonly IReadOnlySet<string> s_cPreprocessor = Set("preproc_ifdef", "preproc_if", "preproc_else", "preproc_elif", "preproc_elifdef");

    /// <summary>C.</summary>
    public static TreeSitterGrammar C { get; } = new()
    {
        Language = LanguageIds.C,
        GrammarId = "C",
        Types = Set("struct_specifier", "union_specifier", "enum_specifier"),
        Members = Set("function_definition", "type_definition", "preproc_function_def"),
        TransparentContainers = Set([.. s_cPreprocessor, "linkage_specification", "declaration_list"]),
        Leading = s_cLeading,
        Blocks = Set("compound_statement", "case_statement", "initializer_list"),
        NameFields = ["name", "declarator"],
    };

    /// <summary>C++ (also used for <c>.h</c>).</summary>
    public static TreeSitterGrammar Cpp { get; } = new()
    {
        Language = LanguageIds.Cpp,
        GrammarId = "C++",
        Namespaces = Set("namespace_definition"),
        Types = Set("class_specifier", "struct_specifier", "union_specifier", "enum_specifier"),
        Members = Set("function_definition", "type_definition", "preproc_function_def", "concept_definition", "alias_declaration"),
        FieldMembers = Set("field_declaration"),
        Wrappers = Set("template_declaration"),
        TransparentContainers = Set([.. s_cPreprocessor, "linkage_specification", "declaration_list"]),
        Leading = Set("comment", "identifier", "access_specifier"),
        Blocks = Set("compound_statement", "case_statement", "initializer_list", "field_declaration_list"),
        NameFields = ["name", "declarator"],
    };

    /// <summary>Go.</summary>
    public static TreeSitterGrammar Go { get; } = new()
    {
        Language = LanguageIds.Go,
        GrammarId = "Go",
        Types = Set("type_declaration"),
        Members = Set("function_declaration", "method_declaration"),
        Leading = Set("comment"),
        FileNamespaces = Set("package_clause"),
        Blocks = Set("block", "statement_list", "expression_switch_statement", "type_switch_statement", "select_statement", "literal_value"),
    };

    /// <summary>Rust.</summary>
    public static TreeSitterGrammar Rust { get; } = new()
    {
        Language = LanguageIds.Rust,
        GrammarId = "Rust",
        Namespaces = Set("mod_item"),
        Types = Set("struct_item", "enum_item", "union_item", "trait_item", "impl_item"),
        Members = Set("function_item", "function_signature_item", "macro_definition", "type_item"),
        FieldMembers = Set("const_item", "static_item", "associated_type"),
        TransparentContainers = Set("foreign_mod_item"),
        Leading = Set("line_comment", "block_comment", "attribute_item", "inner_attribute_item"),
        Blocks = Set("block", "match_block", "declaration_list"),
        NameFields = ["name", "type"],
    };

    /// <summary>Java.</summary>
    public static TreeSitterGrammar Java { get; } = new()
    {
        Language = LanguageIds.Java,
        GrammarId = "Java",
        Types = Set("class_declaration", "interface_declaration", "enum_declaration", "record_declaration", "annotation_type_declaration"),
        Members = Set("method_declaration", "constructor_declaration", "compact_constructor_declaration", "annotation_type_element_declaration"),
        FieldMembers = Set("field_declaration", "constant_declaration", "enum_constant"),
        TransparentContainers = Set("enum_body_declarations"),
        Leading = Set("line_comment", "block_comment"),
        FileNamespaces = Set("package_declaration"),
        Blocks = Set("block", "constructor_body", "switch_block", "switch_block_statement_group", "array_initializer"),
    };

    /// <summary>CSS: rule sets as members, <c>@media</c> and <c>@supports</c> as containers.</summary>
    public static TreeSitterGrammar Css { get; } = new()
    {
        Language = LanguageIds.Css,
        GrammarId = "CSS",
        Types = Set("media_statement", "supports_statement", "at_rule"),
        Members = Set("rule_set", "keyframes_statement"),
        Leading = Set("comment", "js_comment"),
        Blocks = Set("block", "keyframe_block_list"),
        BodyTypes = Set("block"),
    };

    /// <summary>HTML (chunked by <see cref="HtmlChunker"/>).</summary>
    public static TreeSitterGrammar Html { get; } = new()
    {
        Language = LanguageIds.Html,
        GrammarId = "HTML",
        Leading = Set("comment"),
    };
}
