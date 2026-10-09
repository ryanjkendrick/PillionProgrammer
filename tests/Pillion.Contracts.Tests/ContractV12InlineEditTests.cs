using System.Reflection;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Resolvers;

namespace Pillion.Contracts.Tests;

/// <summary>
/// Contract 1.2 (pending): the inline edit service and its DTOs. Additive only, so a 1.1 peer is still compatible; a 1.1
/// VSIX simply never calls <see cref="IInlineEditService"/>.
/// </summary>
public class ContractV12InlineEditTests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(StandardResolver.Instance);

    /// <summary>The wire order of every member. Changing any of these is a major change.</summary>
    public static TheoryData<Type, string, int> Orders => new()
    {
        { typeof(InlineEditRequest), nameof(InlineEditRequest.RequestId), 0 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.FilePath), 1 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.LanguageId), 2 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.ScopeText), 3 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.ScopeStartLine), 4 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.ScopeStartColumn), 5 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.TextBefore), 6 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.TextAfter), 7 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.Instruction), 8 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.Command), 9 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.Diagnostics), 10 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.PreviousProposal), 11 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.ModelId), 12 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.Profile), 13 },
        { typeof(InlineEditRequest), nameof(InlineEditRequest.ConversationId), 14 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.RequestId), 0 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.Text), 1 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.IsFinal), 2 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.Explanation), 3 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.ModelId), 4 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.ProviderId), 5 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.PromptTokens), 6 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.CompletionTokens), 7 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.Error), 8 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.RejectedBlocks), 9 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.Warning), 10 },
        { typeof(InlineEditChunk), nameof(InlineEditChunk.ResetText), 11 },
        { typeof(InlineEditDiagnostic), nameof(InlineEditDiagnostic.Line), 0 },
        { typeof(InlineEditDiagnostic), nameof(InlineEditDiagnostic.Severity), 1 },
        { typeof(InlineEditDiagnostic), nameof(InlineEditDiagnostic.Code), 2 },
        { typeof(InlineEditDiagnostic), nameof(InlineEditDiagnostic.Message), 3 },
    };

    [Theory]
    [MemberData(nameof(Orders))]
    public void Member_HasItsOrder(Type type, string property, int order)
    {
        var member = type.GetProperty(property)?.GetCustomAttribute<DataMemberAttribute>();

        Assert.NotNull(member);
        Assert.Equal(order, member.Order);
    }

    [Fact]
    public void ANewRequest_RoutesByTheTaskClass_AndHasNoCommandOrRefinement()
    {
        var request = new InlineEditRequest();

        Assert.Equal(Profile.Auto, request.Profile);
        Assert.Equal(InlineEditCommand.None, request.Command);
        Assert.Null(request.PreviousProposal);
        Assert.Null(request.ModelId);
        Assert.Empty(request.Diagnostics);
    }

    [Fact]
    public void Diagnostic_IsAnArray_WithTheSeverityAsAnInteger()
    {
        var bytes = MessagePackSerializer.Serialize(new InlineEditDiagnostic { Line = 3, Severity = DiagnosticSeverity.Error, Code = "BC30451", Message = "m" }, Options);

        Assert.Equal("[3,3,\"BC30451\",\"m\"]", MessagePackSerializer.ConvertToJson(bytes, Options));
    }

    [Fact]
    public void EveryMethod_TakesTheTokenLast()
    {
        foreach (var method in typeof(IInlineEditService).GetMethods())
        {
            Assert.Equal(typeof(CancellationToken), method.GetParameters()[^1].ParameterType);
        }

        Assert.Equal(typeof(IAsyncEnumerable<InlineEditChunk>), typeof(IInlineEditService).GetMethod(nameof(IInlineEditService.EditAsync))!.ReturnType);
    }

    [Fact]
    public void AOnePointOnePeer_IsStillCompatible()
    {
        Assert.True(ContractVersion.IsCompatible(1, 1));
    }
}
