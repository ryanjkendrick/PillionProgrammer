using System;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using StreamJsonRpc;

namespace Pillion.VisualStudio.Tests;

public sealed class ChatTextSegmenterTests
{
    [Fact]
    public void PlainText_IsOneTextSegment() =>
        Assert.Equal([new ChatSegment(ChatSegmentKind.Text, "Hello\nworld")], ChatTextSegmenter.Split("Hello\r\nworld"));

    [Fact]
    public void FencedBlocks_AreSplitOutWithTheirLanguage()
    {
        var segments = ChatTextSegmenter.Split("Try this:\n\n```csharp\nvar x = 1;\n\nreturn x;\n```\nThen ~~~\n~~~\nplain\n~~~\nDone.");

        Assert.Equal(
            [
                new ChatSegment(ChatSegmentKind.Text, "Try this:"),
                new ChatSegment(ChatSegmentKind.Code, "var x = 1;\n\nreturn x;", "csharp"),
                new ChatSegment(ChatSegmentKind.Text, "Then ~~~"),
                new ChatSegment(ChatSegmentKind.Code, "plain"),
                new ChatSegment(ChatSegmentKind.Text, "Done."),
            ],
            segments);
        Assert.Equal("Code block, csharp", segments[1].AutomationName);
    }

    [Fact]
    public void AnUnclosedFence_IsCodeWhileItStreams()
    {
        var segments = ChatTextSegmenter.Split("Here:\n```python\nprint(1)");

        var last = segments[segments.Count - 1];
        Assert.Equal(ChatSegmentKind.Code, last.Kind);
        Assert.Equal("print(1)", last.Text);
        Assert.Equal("python", last.Language);
    }

    [Fact]
    public void InlineTripleBackticks_AndLongerClosingFences_AreHandled()
    {
        Assert.Single(ChatTextSegmenter.Split("Use ```x``` inline."));

        var segments = ChatTextSegmenter.Split("````\n```\nnested\n```\n````");
        var code = Assert.Single(segments);
        Assert.Equal("```\nnested\n```", code.Text);
    }

    [Fact]
    public void EmptyText_HasNoSegments() => Assert.Empty(ChatTextSegmenter.Split(string.Empty));

    [Fact]
    public void RpcFailure_DescribesEachKindOfHostError()
    {
        var typed = RpcFailure.Describe(new PillionRpcException(new PillionError { Code = PillionErrorCode.ContextOverflow, Message = "Too long.", RetryHint = "Unpin a file." }));
        Assert.Equal(("Too long.", "Unpin a file.", PillionErrorCode.ContextOverflow, false), (typed.Message, typed.RetryHint, typed.Code, typed.HostLost));

        var restarting = RpcFailure.Describe(new PillionRpcException(new PillionError { Code = PillionErrorCode.HostRestarting, Message = "Restarting." }));
        Assert.True(restarting.HostLost);

        var lost = RpcFailure.Describe(new AggregateException(new ConnectionLostException()));
        Assert.True(lost.HostLost);

        var untyped = RpcFailure.Describe(new RemoteInvocationException("An internal error occurred (ref 1234).", -32000, errorData: null));
        Assert.Equal("An internal error occurred (ref 1234).", untyped.Message);
        Assert.Null(untyped.Code);
    }
}
