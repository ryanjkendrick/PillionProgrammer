// .NET Framework 4.8 lacks these compiler-support types. IsExternalInit enables records; NotNullAttribute is used by
// the AbstractIdeIntegrationTest that Microsoft.VisualStudio.Extensibility.Testing.SourceGenerator generates.
#pragma warning disable SA1403, SA1649, CA1018 // Polyfills for compiler-known types.

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class NotNullAttribute : Attribute
    {
    }
}
