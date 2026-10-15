using System.Collections.Frozen;
using Pillion.ContextEngine.Chunking.TreeSitter;
using static Pillion.ContextEngine.Chunking.TreeSitter.TreeSitterGrammar;

namespace Pillion.ContextEngine.Symbols.TreeSitter;

/// <summary>The symbol descriptions for the tree-sitter grammars Pillion ships (HTML and CSS have their own extractors).</summary>
public static class TreeSitterSymbolGrammars
{
    private static readonly TreeSitterReferenceRule s_callFunction = new(SymbolReferenceKind.Call, TargetField: "function");

    private static readonly IReadOnlyDictionary<string, TreeSitterReferenceRule> s_jsReferences = Map(
        ("call_expression", s_callFunction),
        ("new_expression", new TreeSitterReferenceRule(SymbolReferenceKind.Call, TargetField: "constructor") { IsConstruction = true }),
        ("decorator", new TreeSitterReferenceRule(SymbolReferenceKind.Call)));

    private static readonly IReadOnlyDictionary<string, SymbolKind> s_jsDeclarations = Map(
        ("lexical_declaration", SymbolKind.Variable),
        ("variable_declaration", SymbolKind.Variable));

    /// <summary>TypeScript.</summary>
    public static TreeSitterSymbolGrammar TypeScript { get; } = new()
    {
        Grammar = TreeSitterGrammars.TypeScript,
        Definitions = Map(
            ("class_declaration", new TreeSitterDefinitionRule(SymbolKind.Class)),
            ("abstract_class_declaration", new TreeSitterDefinitionRule(SymbolKind.Class)),
            ("interface_declaration", new TreeSitterDefinitionRule(SymbolKind.Interface)),
            ("enum_declaration", new TreeSitterDefinitionRule(SymbolKind.Enum)),
            ("internal_module", new TreeSitterDefinitionRule(SymbolKind.Namespace)),
            ("module", new TreeSitterDefinitionRule(SymbolKind.Module)),
            ("type_alias_declaration", new TreeSitterDefinitionRule(SymbolKind.TypeAlias)),
            ("function_declaration", new TreeSitterDefinitionRule(SymbolKind.Function)),
            ("generator_function_declaration", new TreeSitterDefinitionRule(SymbolKind.Function)),
            ("function_signature", new TreeSitterDefinitionRule(SymbolKind.Function)),
            ("method_definition", new TreeSitterDefinitionRule(SymbolKind.Method)),
            ("method_signature", new TreeSitterDefinitionRule(SymbolKind.Method)),
            ("abstract_method_signature", new TreeSitterDefinitionRule(SymbolKind.Method)),
            ("public_field_definition", new TreeSitterDefinitionRule(SymbolKind.Field)),
            ("property_signature", new TreeSitterDefinitionRule(SymbolKind.Property))),
        Declarations = s_jsDeclarations,
        Declarators = Set("variable_declarator"),
        References = s_jsReferences,
        TypeIdentifiers = Set("type_identifier"),
        QualifiedTypes = Set("nested_type_identifier"),
        Identifiers = Set("identifier", "type_identifier", "property_identifier", "shorthand_property_identifier"),
        QualifiedNames = Set("member_expression", "nested_identifier"),
        InheritanceRegions = Set("class_heritage", "extends_type_clause"),
        ImportRegions = Set("import_statement"),
        TypeArgumentRegions = Set("type_arguments"),
        SkippedRegions = Set("type_parameters"),
        ModuleStrings = Set("string"),
        FileModule = FileModuleNaming.Stem,
        ConstructorNames = Set("constructor"),
        IgnoredNames = Set("this", "super"),
        RequireFunctions = Set("require"),
    };

    /// <summary>TypeScript with JSX; the same node types as <see cref="TypeScript"/>.</summary>
    public static TreeSitterSymbolGrammar Tsx { get; } = TypeScript with { Grammar = TreeSitterGrammars.Tsx };

    /// <summary>JavaScript (and JSX).</summary>
    public static TreeSitterSymbolGrammar JavaScript { get; } = new()
    {
        Grammar = TreeSitterGrammars.JavaScript,
        Definitions = Map(
            ("class_declaration", new TreeSitterDefinitionRule(SymbolKind.Class)),
            ("function_declaration", new TreeSitterDefinitionRule(SymbolKind.Function)),
            ("generator_function_declaration", new TreeSitterDefinitionRule(SymbolKind.Function)),
            ("method_definition", new TreeSitterDefinitionRule(SymbolKind.Method)),
            ("field_definition", new TreeSitterDefinitionRule(SymbolKind.Field))),
        Declarations = s_jsDeclarations,
        Declarators = Set("variable_declarator"),
        References = s_jsReferences,
        Identifiers = Set("identifier", "property_identifier", "shorthand_property_identifier"),
        QualifiedNames = Set("member_expression"),
        InheritanceRegions = Set("class_heritage"),
        ImportRegions = Set("import_statement"),
        ModuleStrings = Set("string"),
        FileModule = FileModuleNaming.Stem,
        ConstructorNames = Set("constructor"),
        IgnoredNames = Set("this", "super"),
        RequireFunctions = Set("require"),
    };

    /// <summary>Python.</summary>
    public static TreeSitterSymbolGrammar Python { get; } = new()
    {
        Grammar = TreeSitterGrammars.Python,
        Definitions = Map(
            ("class_definition", new TreeSitterDefinitionRule(SymbolKind.Class)),
            ("function_definition", new TreeSitterDefinitionRule(SymbolKind.Function))),
        Declarations = Map(("assignment", SymbolKind.Variable)),
        References = Map(
            ("call", s_callFunction),
            ("decorator", new TreeSitterReferenceRule(SymbolReferenceKind.Call))),
        Identifiers = Set("identifier"),
        QualifiedNames = Set("attribute", "dotted_name"),
        InheritanceFields = Map(("class_definition", "superclasses")),
        ImportRegions = Set("import_statement", "import_from_statement"),
        TypeRegions = Set("type"),
        FileModule = FileModuleNaming.Stem,
        ConstructorNames = Set("__init__"),
        IgnoredNames = Set("self", "cls", "super"),
    };

    private static readonly IReadOnlyDictionary<string, TreeSitterDefinitionRule> s_cDefinitions = Map(
        ("function_definition", new TreeSitterDefinitionRule(SymbolKind.Function)),
        ("struct_specifier", new TreeSitterDefinitionRule(SymbolKind.Struct) { RequiredField = "body" }),
        ("union_specifier", new TreeSitterDefinitionRule(SymbolKind.Type) { RequiredField = "body" }),
        ("enum_specifier", new TreeSitterDefinitionRule(SymbolKind.Enum) { RequiredField = "body" }),
        ("enumerator", new TreeSitterDefinitionRule(SymbolKind.EnumMember)),
        ("type_definition", new TreeSitterDefinitionRule(SymbolKind.TypeAlias)),
        ("preproc_function_def", new TreeSitterDefinitionRule(SymbolKind.Macro)));

    private static readonly IReadOnlyDictionary<string, SymbolKind> s_cDeclarations = Map(
        ("declaration", SymbolKind.Variable),
        ("field_declaration", SymbolKind.Field));

    /// <summary>C.</summary>
    public static TreeSitterSymbolGrammar C { get; } = new()
    {
        Grammar = TreeSitterGrammars.C,
        Definitions = s_cDefinitions,
        Declarations = s_cDeclarations,
        Declarators = Set("init_declarator"),
        References = Map(("call_expression", s_callFunction)),
        TypeIdentifiers = Set("type_identifier"),
        Identifiers = Set("identifier", "field_identifier", "type_identifier"),
        QualifiedNames = Set("field_expression"),
        ImportRegions = Set("preproc_include"),
        ModuleStrings = Set("string_literal", "system_lib_string"),
        FileModule = FileModuleNaming.FileName,
    };

    /// <summary>C++ (also used for <c>.h</c>).</summary>
    public static TreeSitterSymbolGrammar Cpp { get; } = new()
    {
        Grammar = TreeSitterGrammars.Cpp,
        Definitions = Map(
            [.. s_cDefinitions.Select(pair => (pair.Key, pair.Value)),
            ("class_specifier", new TreeSitterDefinitionRule(SymbolKind.Class) { RequiredField = "body" }),
            ("namespace_definition", new TreeSitterDefinitionRule(SymbolKind.Namespace)),
            ("alias_declaration", new TreeSitterDefinitionRule(SymbolKind.TypeAlias)),
            ("concept_definition", new TreeSitterDefinitionRule(SymbolKind.Type))]),
        Declarations = s_cDeclarations,
        Declarators = Set("init_declarator"),
        References = Map(
            ("call_expression", s_callFunction),
            ("new_expression", new TreeSitterReferenceRule(SymbolReferenceKind.Call, TargetField: "type") { IsConstruction = true })),
        TypeIdentifiers = Set("type_identifier"),
        Identifiers = Set("identifier", "field_identifier", "type_identifier", "namespace_identifier"),
        QualifiedNames = Set("field_expression", "qualified_identifier"),
        InheritanceRegions = Set("base_class_clause"),
        ImportRegions = Set("preproc_include", "using_declaration"),
        TypeArgumentRegions = Set("template_argument_list"),
        SkippedRegions = Set("template_parameter_list"),
        ModuleStrings = Set("string_literal", "system_lib_string"),
        QualifiedDeclarators = Set("qualified_identifier"),
        FileModule = FileModuleNaming.FileName,
        IgnoredNames = Set("this"),
    };

    /// <summary>Go.</summary>
    public static TreeSitterSymbolGrammar Go { get; } = new()
    {
        Grammar = TreeSitterGrammars.Go,
        Definitions = Map(
            ("function_declaration", new TreeSitterDefinitionRule(SymbolKind.Function)),
            ("method_declaration", new TreeSitterDefinitionRule(SymbolKind.Method) { ContainerField = "receiver" }),
            ("type_spec", new TreeSitterDefinitionRule(SymbolKind.Type)
            {
                KindField = "type",
                KindByFieldType = Map(("struct_type", SymbolKind.Struct), ("interface_type", SymbolKind.Interface)),
            }),
            ("type_alias", new TreeSitterDefinitionRule(SymbolKind.TypeAlias)),
            ("field_declaration", new TreeSitterDefinitionRule(SymbolKind.Field)),
            ("method_elem", new TreeSitterDefinitionRule(SymbolKind.Method)),
            ("method_spec", new TreeSitterDefinitionRule(SymbolKind.Method))),
        Declarations = Map(("const_declaration", SymbolKind.Constant), ("var_declaration", SymbolKind.Variable)),
        Declarators = Set("const_spec", "var_spec"),
        References = Map(("call_expression", s_callFunction)),
        TypeIdentifiers = Set("type_identifier"),
        QualifiedTypes = Set("qualified_type"),
        Identifiers = Set("identifier", "field_identifier", "package_identifier", "type_identifier"),
        QualifiedNames = Set("selector_expression"),
        ImportRegions = Set("import_declaration"),
        TypeArgumentRegions = Set("type_arguments"),
        SkippedRegions = Set("type_parameter_list"),
        ModuleStrings = Set("interpreted_string_literal", "raw_string_literal"),
    };

    /// <summary>Rust.</summary>
    public static TreeSitterSymbolGrammar Rust { get; } = new()
    {
        Grammar = TreeSitterGrammars.Rust,
        Definitions = Map(
            ("struct_item", new TreeSitterDefinitionRule(SymbolKind.Struct)),
            ("enum_item", new TreeSitterDefinitionRule(SymbolKind.Enum)),
            ("union_item", new TreeSitterDefinitionRule(SymbolKind.Type)),
            ("trait_item", new TreeSitterDefinitionRule(SymbolKind.Interface)),
            ("mod_item", new TreeSitterDefinitionRule(SymbolKind.Module)),
            ("function_item", new TreeSitterDefinitionRule(SymbolKind.Function)),
            ("function_signature_item", new TreeSitterDefinitionRule(SymbolKind.Function)),
            ("macro_definition", new TreeSitterDefinitionRule(SymbolKind.Macro)),
            ("type_item", new TreeSitterDefinitionRule(SymbolKind.TypeAlias)),
            ("associated_type", new TreeSitterDefinitionRule(SymbolKind.TypeAlias)),
            ("const_item", new TreeSitterDefinitionRule(SymbolKind.Constant)),
            ("static_item", new TreeSitterDefinitionRule(SymbolKind.Variable)),
            ("enum_variant", new TreeSitterDefinitionRule(SymbolKind.EnumMember)),
            ("field_declaration", new TreeSitterDefinitionRule(SymbolKind.Field))),
        Containers = Set("impl_item"),
        References = Map(
            ("call_expression", s_callFunction),
            ("macro_invocation", new TreeSitterReferenceRule(SymbolReferenceKind.Call, TargetField: "macro"))),
        TypeIdentifiers = Set("type_identifier"),
        QualifiedTypes = Set("scoped_type_identifier"),
        Identifiers = Set("identifier", "type_identifier", "field_identifier"),
        QualifiedNames = Set("scoped_identifier", "field_expression"),
        InheritanceFields = Map(("impl_item", "trait")),
        ImportRegions = Set("use_declaration"),
        TypeArgumentRegions = Set("type_arguments"),
        SkippedRegions = Set("type_parameters"),
        FileModule = FileModuleNaming.Stem,
        IgnoredNames = Set("Self", "self", "crate", "super"),
    };

    /// <summary>Java.</summary>
    public static TreeSitterSymbolGrammar Java { get; } = new()
    {
        Grammar = TreeSitterGrammars.Java,
        Definitions = Map(
            ("class_declaration", new TreeSitterDefinitionRule(SymbolKind.Class)),
            ("interface_declaration", new TreeSitterDefinitionRule(SymbolKind.Interface)),
            ("enum_declaration", new TreeSitterDefinitionRule(SymbolKind.Enum)),
            ("record_declaration", new TreeSitterDefinitionRule(SymbolKind.Class)),
            ("annotation_type_declaration", new TreeSitterDefinitionRule(SymbolKind.Interface)),
            ("method_declaration", new TreeSitterDefinitionRule(SymbolKind.Method)),
            ("constructor_declaration", new TreeSitterDefinitionRule(SymbolKind.Constructor)),
            ("compact_constructor_declaration", new TreeSitterDefinitionRule(SymbolKind.Constructor)),
            ("annotation_type_element_declaration", new TreeSitterDefinitionRule(SymbolKind.Method)),
            ("enum_constant", new TreeSitterDefinitionRule(SymbolKind.EnumMember))),
        Declarations = Map(("field_declaration", SymbolKind.Field), ("constant_declaration", SymbolKind.Constant)),
        Declarators = Set("variable_declarator"),
        References = Map(
            ("method_invocation", new TreeSitterReferenceRule(SymbolReferenceKind.Call, NameField: "name", QualifierField: "object")),
            ("object_creation_expression", new TreeSitterReferenceRule(SymbolReferenceKind.Call, TargetField: "type") { IsConstruction = true }),
            ("marker_annotation", new TreeSitterReferenceRule(SymbolReferenceKind.TypeUse, TargetField: "name")),
            ("annotation", new TreeSitterReferenceRule(SymbolReferenceKind.TypeUse, TargetField: "name"))),
        TypeIdentifiers = Set("type_identifier"),
        QualifiedTypes = Set("scoped_type_identifier"),
        Identifiers = Set("identifier", "type_identifier"),
        QualifiedNames = Set("scoped_identifier", "field_access"),
        InheritanceRegions = Set("superclass", "super_interfaces", "extends_interfaces"),
        ImportRegions = Set("import_declaration"),
        TypeArgumentRegions = Set("type_arguments"),
        SkippedRegions = Set("type_parameters"),
        IgnoredNames = Set("this", "super"),
    };

    /// <summary>Every description in this class, for tests and the catalog.</summary>
    public static IReadOnlyList<TreeSitterSymbolGrammar> All { get; } = [TypeScript, Tsx, JavaScript, Python, C, Cpp, Go, Rust, Java];

    private static FrozenDictionary<string, T> Map<T>(params (string Key, T Value)[] entries) =>
        entries.ToFrozenDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}
