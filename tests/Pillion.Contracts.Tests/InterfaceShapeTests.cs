namespace Pillion.Contracts.Tests;

/// <summary>
/// Guards against accidental contract changes. If you change an interface on purpose, update the expected method
/// names here and bump <see cref="ContractVersion"/> (see src/Pillion.Contracts/README.md).
/// </summary>
public class InterfaceShapeTests
{
    private static readonly Dictionary<Type, string[]> ExpectedMethods = new()
    {
        // VSIX to host.
        [typeof(IModelService)] = ["CompleteAsync", "ChatAsync", "CancelAsync", "ListModelsAsync"],
        [typeof(IContextService)] = ["SearchAsync", "ResolveMentionAsync", "GetIndexStatusAsync", "PauseIndexingAsync"],
        [typeof(IAgentService)] = ["StartSessionAsync", "ApproveAsync", "DenyAsync", "StopAsync", "RevertAsync"],
        [typeof(ISettingsService)] = ["GetAsync", "SetAsync", "TestProviderAsync"],
        [typeof(IHostLifecycleService)] = ["HandshakeAsync", "GetStateAsync", "ShutdownAsync"],
        [typeof(IDiagnosticsService)] = ["GetAutocompleteCountersAsync"],

        // Host to VSIX.
        [typeof(IEditorService)] = ["ApplyChangesetAsync", "ShowPreviewAsync", "GetOpenDocumentsAsync"],
        [typeof(IApprovalService)] = ["RequestApprovalAsync"],
        [typeof(IWorkspaceQueryService)] = ["FindReferencesAsync", "GetDiagnosticsAsync", "GetSymbolAsync", "RenameSymbolAsync", "RunTestsAsync"],
    };

    public static TheoryData<Type> Interfaces => new(ExpectedMethods.Keys);

    [Fact]
    public void EveryPublicInterface_IsCovered()
    {
        var actual = typeof(ContractVersion).Assembly.GetExportedTypes()
            .Where(t => t.IsInterface)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();

        var expected = ExpectedMethods.Keys.OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Interfaces))]
    public void Interface_HasExactlyTheExpectedMethods(Type contract)
    {
        var actual = contract.GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
        var expected = ExpectedMethods[contract].Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Interfaces))]
    public void Interface_HasNoPropertiesOrEvents(Type contract)
    {
        Assert.Empty(contract.GetProperties());
        Assert.Empty(contract.GetEvents());
    }

    [Theory]
    [MemberData(nameof(Interfaces))]
    public void EveryMethod_TakesTrailingCancellationToken(Type contract)
    {
        foreach (var method in contract.GetMethods())
        {
            var parameters = method.GetParameters();
            Assert.True(
                parameters.Length > 0 && parameters[^1].ParameterType == typeof(CancellationToken),
                $"{contract.Name}.{method.Name} must take a CancellationToken as its last parameter.");
            Assert.Equal(1, parameters.Count(p => p.ParameterType == typeof(CancellationToken)));
        }
    }

    [Theory]
    [MemberData(nameof(Interfaces))]
    public void EveryMethod_ReturnsTaskOrAsyncEnumerable(Type contract)
    {
        foreach (var method in contract.GetMethods())
        {
            var returnType = method.ReturnType;
            var isTask = returnType == typeof(Task)
                || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>));
            var isStream = returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>);

            Assert.True(isTask || isStream, $"{contract.Name}.{method.Name} returns {returnType}, expected Task, Task<T> or IAsyncEnumerable<T>.");
            Assert.True(method.Name.EndsWith("Async", StringComparison.Ordinal), $"{contract.Name}.{method.Name} must end with Async.");
        }
    }

    [Fact]
    public void ContractsAssembly_DoesNotReferenceSerializerOrRpcLibraries()
    {
        var references = typeof(ContractVersion).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.DoesNotContain(references, name => name!.StartsWith("MessagePack", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name!.StartsWith("StreamJsonRpc", StringComparison.Ordinal));
    }

    [Fact]
    public void PermissionTier_HasExpectedMembers()
    {
        Assert.Equal(["T0", "T1", "T2", "T3"], Enum.GetNames<PermissionTier>());
    }

    [Fact]
    public void PillionErrorCode_HasExpectedMembers()
    {
        Assert.Equal(
            ["ProviderUnavailable", "ModelNotInstalled", "ContextOverflow", "PolicyDenied", "ApprovalDenied", "HostRestarting", "Cancelled"],
            Enum.GetNames<PillionErrorCode>());
    }

    [Fact]
    public void HostState_HasExpectedMembers()
    {
        Assert.Equal(["Starting", "Ready", "Degraded", "Restarting", "Failed"], Enum.GetNames<HostState>());
    }

    [Fact]
    public void TaskClass_HasExpectedMembers()
    {
        Assert.Equal(["Autocomplete", "InlineEdit", "Chat", "Agent", "Embedding", "Vision", "Summarise"], Enum.GetNames<TaskClass>());
    }

    [Fact]
    public void Profile_HasExpectedMembers()
    {
        Assert.Equal(["Fast", "Balanced", "Frontier", "LocalFast", "LocalStrong", "Auto"], Enum.GetNames<Profile>());
    }

    [Fact]
    public void PillionRpcException_CarriesError()
    {
        var error = new PillionError { Code = PillionErrorCode.PolicyDenied, Message = "Blocked by policy." };
        var inner = new InvalidOperationException("inner");

        var exception = new PillionRpcException(error, inner);

        Assert.Same(error, exception.Error);
        Assert.Equal("Blocked by policy.", exception.Message);
        Assert.Same(inner, exception.InnerException);
        Assert.Throws<ArgumentNullException>(() => new PillionRpcException(null!));
    }
}
