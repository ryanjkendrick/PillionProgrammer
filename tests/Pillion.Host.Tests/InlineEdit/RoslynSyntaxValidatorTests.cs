using Pillion.Host.InlineEdit;

namespace Pillion.Host.Tests.InlineEdit;

public sealed class RoslynSyntaxValidatorTests
{
    private readonly RoslynSyntaxValidator _validator = RoslynSyntaxValidator.Instance;

    [Theory]
    [InlineData("csharp", true)]
    [InlineData("CSharp", true)]
    [InlineData("vb", true)]
    [InlineData("visualbasic", true)]
    [InlineData("fsharp", false)]
    [InlineData("python", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Supports_CSharpAndVisualBasicOnly(string? languageId, bool expected)
    {
        Assert.Equal(expected, _validator.Supports(languageId));
    }

    [Fact]
    public void CSharp_ValidCode_HasNoErrors_EvenWithUnknownTypes()
    {
        Assert.Empty(_validator.GetErrors("class C { void M() { Unknown.Call(x); } }", "csharp", CancellationToken.None));
    }

    [Fact]
    public void CSharp_AMissingSemicolon_IsReportedOnItsLine()
    {
        var error = Assert.Single(_validator.GetErrors("class C\n{\n    void M()\n    {\n        Call()\n    }\n}\n", "csharp", CancellationToken.None));

        Assert.Equal(4, error.Line);
        Assert.Equal("CS1002", error.Code);
        Assert.Equal("; expected", error.Message);
    }

    [Fact]
    public void CSharp_PreviewSyntax_Parses()
    {
        Assert.Empty(_validator.GetErrors("var xs = [1, 2, 3];\nstring s = \"\"\"\n  raw\n  \"\"\";\n", "csharp", CancellationToken.None));
    }

    [Fact]
    public void VisualBasic_ValidAndBrokenCode()
    {
        Assert.Empty(_validator.GetErrors("Class C\n    Function F() As Integer\n        Return 1\n    End Function\nEnd Class\n", "vb", CancellationToken.None));
        Assert.NotEmpty(_validator.GetErrors("Class C\n    Function F() As Integer\n        Return (1\n    End Function\nEnd Class\n", "vb", CancellationToken.None));
    }

    [Fact]
    public void AnUnsupportedLanguage_Throws()
    {
        Assert.Throws<NotSupportedException>(() => _validator.GetErrors("x", "python", CancellationToken.None));
    }
}
