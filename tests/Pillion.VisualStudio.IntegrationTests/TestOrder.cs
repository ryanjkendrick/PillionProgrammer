using System;
using System.Collections.Generic;
using System.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Sets the order of a test within its class (lower first). The tests in a class share one Visual Studio instance
/// and one Pillion Host, so order matters: the package-load test must see the package load for the first time.
/// </summary>
/// <param name="order">The position.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestOrderAttribute(int order) : Attribute
{
    /// <summary>Gets the position.</summary>
    public int Order { get; } = order;
}

/// <summary>Orders test cases by <see cref="TestOrderAttribute"/>, then by display name (which carries the Visual Studio version).</summary>
public sealed class TestOrderer : ITestCaseOrderer
{
    /// <summary>The orderer's type name, for <see cref="TestCaseOrdererAttribute"/>.</summary>
    public const string TypeName = "Pillion.VisualStudio.IntegrationTests.TestOrderer";

    /// <summary>The orderer's assembly name, for <see cref="TestCaseOrdererAttribute"/>.</summary>
    public const string AssemblyName = "Pillion.VisualStudio.IntegrationTests";

    /// <inheritdoc/>
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase =>
        testCases
            .OrderBy(t => OrderOf(t))
            .ThenBy(t => t.DisplayName, StringComparer.Ordinal)
            .ToList();

    private static int OrderOf(ITestCase testCase) =>
        testCase.TestMethod.Method
            .GetCustomAttributes(typeof(TestOrderAttribute).AssemblyQualifiedName)
            .Select(a => a.GetConstructorArguments().OfType<int>().FirstOrDefault())
            .DefaultIfEmpty(int.MaxValue)
            .First();
}
