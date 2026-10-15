using Xunit.Abstractions;

namespace Pillion.ContextEngine.Tests.Symbols;

/// <summary>Extracts every fixture, checks the invariants and logs the symbols (useful when tuning a grammar description).</summary>
public sealed class SymbolDumpTests(ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(FixtureDumpTests.Fixtures), MemberType = typeof(FixtureDumpTests))]
    public void Fixture_symbols_satisfy_the_invariants(string fixture)
    {
        var extraction = SymbolFixture.Extract(fixture);
        output.WriteLine(SymbolFixture.Describe(extraction));
    }
}
