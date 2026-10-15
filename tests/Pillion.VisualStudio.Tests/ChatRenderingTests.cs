using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Markdown;
using Pillion.VisualStudio.ToolWindows;
using Xunit.Abstractions;

namespace Pillion.VisualStudio.Tests;

/// <summary>
/// The VSIX's Markdown renderer (compiled into this assembly from src/Pillion.VisualStudio/ToolWindows/Markdown) on a WPF
/// thread, without Visual Studio: code blocks use the monospace fallback view, theme keys are plain strings.
/// </summary>
[Collection(RealProcessTestGroup.Name)]
public sealed class ChatRenderingTests
{
    private const string Sample =
        "# Title\n\nSome **bold** and `code` with a [safe link](https://example.com/docs) and a [bad one](javascript:alert(1)).\n\n"
        + "![diagram](https://example.com/d.png)\n\n- one\n- two\n\n| A | B |\n| --- | ---: |\n| 1 | 2 |\n\n"
        + "```csharp\nvar x = 1;\n```\n\n> quoted\n\n---\n\nEnd.";

    private readonly ITestOutputHelper _output;

    public ChatRenderingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Blocks_MapToThemedWpfElements() => Sta.Run(() =>
    {
        var opened = new List<Uri>();
        var context = Context(opened);
        context.InsertAtCaret = _ => string.Empty;
        var panel = new StackPanel();
        using var renderer = new MarkdownBlockRenderer(panel, context);

        renderer.Update(ChatMarkdownParser.Parse(Sample).Blocks);

        var elements = renderer.Elements;
        Assert.Equal(panel.Children.Cast<UIElement>(), elements);
        Assert.Equal(9, elements.Count);

        var heading = Assert.IsType<RichTextBox>(elements[0]);
        Assert.True(heading.FontSize > 12);
        Assert.Equal("Heading level 1: Title", AutomationProperties.GetName(heading));
        Assert.True(heading.IsReadOnly);

        var paragraph = Assert.IsType<RichTextBox>(elements[1]);
        var inlines = Flatten(paragraph.Document).ToList();
        Assert.Contains(inlines, i => i is Bold);
        Assert.Contains(inlines, i => i is Run run && run.Text == "code" && run.FontFamily.Source.Contains("Consolas"));
        var link = Assert.Single(inlines.OfType<Hyperlink>());
        Assert.Equal("safe link", new TextRange(link.ContentStart, link.ContentEnd).Text);
        Assert.Equal("https://example.com/docs", AutomationProperties.GetHelpText(link));
        Assert.DoesNotContain(inlines.OfType<Hyperlink>(), h => new TextRange(h.ContentStart, h.ContentEnd).Text == "bad one");
        var inert = inlines.OfType<Span>().Single(s => s is not Hyperlink && s is not Bold && new TextRange(s.ContentStart, s.ContentEnd).Text == "bad one");
        Assert.Contains("javascript:alert(1)", (string)inert.ToolTip, StringComparison.Ordinal);

        // Clicking the safe link asks the host (which confirms before opening); nothing opens by itself.
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, link));
        Assert.Equal([new Uri("https://example.com/docs")], opened);

        // Images are never fetched: alt text only, and no Image element anywhere.
        var image = Assert.IsType<RichTextBox>(elements[2]);
        Assert.Equal("[Image: diagram]", new TextRange(image.Document.ContentStart, image.Document.ContentEnd).Text.Trim());
        Assert.Empty(Descendants(panel).OfType<Image>());

        var list = Assert.IsType<StackPanel>(elements[3]);
        Assert.Equal("List, 2 items", AutomationProperties.GetName(list));
        Assert.Equal(2, list.Children.Count);

        var table = Assert.IsType<Border>(elements[4]);
        Assert.Equal("Table, 2 rows, 2 columns", AutomationProperties.GetName(table));
        var grid = Assert.IsType<Grid>(((ScrollViewer)table.Child).Content);
        Assert.Equal(4, grid.Children.Count);
        var header = (TextBlock)((Border)grid.Children[0]).Child;
        Assert.Equal(FontWeights.SemiBold, header.FontWeight);
        Assert.Equal(TextAlignment.Right, ((TextBlock)((Border)grid.Children[3]).Child).TextAlignment);

        var code = Assert.IsType<Border>(elements[5]);
        Assert.Equal("C# code block", AutomationProperties.GetName(code));
        var buttons = Descendants(code).OfType<Button>().ToList();
        Assert.Equal(["Copy C# code", "Insert C# code at cursor"], buttons.Select(AutomationProperties.GetName).OrderBy(n => n));
        Assert.All(buttons, b => Assert.True(b.Focusable && b.IsTabStop));
        Assert.Equal("var x = 1;", Descendants(code).OfType<TextBox>().Single().Text);

        Assert.Equal("Quote", AutomationProperties.GetName(Assert.IsType<Border>(elements[6])));
        Assert.Equal("Separator", AutomationProperties.GetName(Assert.IsType<Border>(elements[7])));
        Assert.All(elements, e => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(e))));
    });

    [Fact]
    public void CodeButtons_CopyAndInsertTheBlocksCode() => Sta.Run(() =>
    {
        string? copied = null;
        string? inserted = null;
        var context = Context([]);
        context.CopyCode = text => { copied = text; return "Copied."; };
        context.InsertAtCaret = text => { inserted = text; return "Inserted into Program.cs."; };
        var panel = new StackPanel();
        using var renderer = new MarkdownBlockRenderer(panel, context);
        renderer.Update(ChatMarkdownParser.Parse("```cs\nConsole.WriteLine(1);\nreturn 0;\n```").Blocks);
        var buttons = Descendants(panel).OfType<Button>().ToList();
        var copy = buttons.Single(b => AutomationProperties.GetName(b) == "Copy C# code");
        var insert = buttons.Single(b => AutomationProperties.GetName(b) == "Insert C# code at cursor");

        copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, copy));
        Assert.Equal("Console.WriteLine(1);\nreturn 0;", copied);
        Assert.Contains(Descendants(panel).OfType<TextBlock>(), t => t.Text == "Copied.");

        insert.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, insert));
        Assert.Equal("Console.WriteLine(1);\nreturn 0;", inserted);
        Assert.Contains(Descendants(panel).OfType<TextBlock>(), t => t.Text == "Inserted into Program.cs.");
    });

    [Fact]
    public void CodeButtons_InsertIsHiddenWhenTheHostCannotInsert() => Sta.Run(() =>
    {
        var panel = new StackPanel();
        using var renderer = new MarkdownBlockRenderer(panel, Context([]));
        renderer.Update(ChatMarkdownParser.Parse("```\nplain\n```").Blocks);

        Assert.Equal(["Copy Plain text code"], Descendants(panel).OfType<Button>().Select(AutomationProperties.GetName));
    });

    [Fact]
    public void Streaming_KeepsEarlierElementsAndUpdatesTheTailInPlace() => Sta.Run(() =>
    {
        var panel = new StackPanel();
        using var renderer = new MarkdownBlockRenderer(panel, Context([]));

        renderer.Update(ChatMarkdownParser.Parse("# Title\n\nFirst para").Blocks);
        var heading = renderer.Elements[0];
        var paragraph = renderer.Elements[1];

        var plan = renderer.Update(ChatMarkdownParser.Parse("# Title\n\nFirst paragraph, longer now").Blocks);
        Assert.True(plan.UpdateFirstInPlace);
        Assert.Same(heading, renderer.Elements[0]);
        Assert.Same(paragraph, renderer.Elements[1]);
        var box = (RichTextBox)paragraph;
        Assert.Equal("First paragraph, longer now", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.TrimEnd());

        renderer.Update(ChatMarkdownParser.Parse("# Title\n\nFirst paragraph, longer now\n\n```csharp\nvar a").Blocks);
        var code = renderer.Elements[2];
        var codeBox = Descendants(code).OfType<TextBox>().Single();
        renderer.Update(ChatMarkdownParser.Parse("# Title\n\nFirst paragraph, longer now\n\n```csharp\nvar a = 1;\n```").Blocks);
        Assert.Same(heading, renderer.Elements[0]);
        Assert.Same(paragraph, renderer.Elements[1]);
        Assert.Same(code, renderer.Elements[2]);
        Assert.Same(codeBox, Descendants(renderer.Elements[2]).OfType<TextBox>().Single());
        Assert.Equal("var a = 1;", codeBox.Text);

        // A growing list keeps its earlier items.
        renderer.Update(ChatMarkdownParser.Parse("- one\n- tw").Blocks);
        var list = (StackPanel)renderer.Elements[0];
        var firstItem = list.Children[0];
        renderer.Update(ChatMarkdownParser.Parse("- one\n- two\n- three").Blocks);
        Assert.Same(list, renderer.Elements[0]);
        Assert.Same(firstItem, list.Children[0]);
        Assert.Equal(3, list.Children.Count);
    });

    [Fact]
    public void MessageView_RecyclingAContainerReattachesTheCachedElements() => Sta.Run(() =>
    {
        var context = Context([]);
        using var cache = new MessageRenderCache(context);
        var first = Reply("First **reply**");
        var second = Reply("Second reply");
        var view = new MarkdownMessageView();
        var other = new MarkdownMessageView();
        MarkdownMessageView.SetCache(view, cache);
        MarkdownMessageView.SetCache(other, cache);

        view.Turn = first;
        var firstRoot = view.Child;
        view.Turn = second;
        other.Turn = first;

        Assert.Same(firstRoot, other.Child);
        Assert.NotSame(firstRoot, view.Child);
        Assert.Equal(2, cache.Count);

        // A streamed append updates the attached elements.
        first.Append(" and more");
        var box = (RichTextBox)((Panel)other.Child).Children[0];
        Assert.Equal("First reply and more", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.TrimEnd());

        cache.Retain([second]);
        Assert.Null(other.Child);
        Assert.Equal(1, cache.Count);
    });

    /// <summary>
    /// The incremental-render budget, end to end: a 2,000-token reply streamed by the real fake host process (its
    /// <c>/long</c> trigger) through <see cref="ChatViewModel"/> (50 ms flushes) into a rendered, laid-out message on a WPF
    /// dispatcher thread. Each refresh is timed from the turn's change notification to the end of layout.
    /// </summary>
    [Fact]
    public void Streaming_A2000TokenReplyFromTheFakeHostRendersEachChunkWithinBudget()
    {
        var path = FakeHostPath;
        Assert.True(File.Exists(path), "Build tests/Pillion.FakeHost first: " + path);
        Environment.SetEnvironmentVariable("PILLION_FAKEHOST_MODE", "quiet");
        Environment.SetEnvironmentVariable("PILLION_FAKEHOST_CHUNK_DELAY_MS", "2");
        var logDirectory = Path.Combine(Path.GetTempPath(), "pillion-render-tests-" + Guid.NewGuid().ToString("N"));
        var refreshes = new List<double>();
        var layouts = new List<double>();
        var length = 0;
        try
        {
            Sta.RunOnDispatcher(async ui =>
            {
                var log = new PillionLog(logDirectory);
                using var background = await Task.Run(() => new JoinableTaskContext());
                using var supervisor = new HostSupervisor(
                    new HostProcessLauncher(new HostLauncherOptions(path, typeof(HostSupervisor).Assembly.Location, HostLocalTargets.CreateNotImplemented()) { LogDirectory = logDirectory }, log),
                    background.Factory,
                    log,
                    SystemClock.Instance,
                    new RestartPolicy());
                supervisor.Start();
                await supervisor.WhenReadyAsync(CancellationToken.None).WithTimeout(TimeSpan.FromSeconds(30));

                using var chat = new ChatViewModel(supervisor, ui.Factory, new ChatViewModelOptions { Log = log });
                chat.Start();
                await Wait.UntilAsync(() => chat.ModelPicker.Models.Count == 3, "the fake host's models are listed", timeoutMs: 30000);

                using var cache = new MessageRenderCache(Context([]));
                var host = new MarkdownMessageView { Width = 420 };
                MarkdownMessageView.SetCache(host, cache);
                var watch = new System.Diagnostics.Stopwatch();
                cache.Created += (_, message) => message.Rendered += (_, e) =>
                {
                    // Time the layout of the updated message too, as the window would on its next frame.
                    watch.Restart();
                    host.Measure(new Size(420, double.PositiveInfinity));
                    host.Arrange(new Rect(host.DesiredSize));
                    host.UpdateLayout();
                    watch.Stop();
                    refreshes.Add(e.Elapsed.TotalMilliseconds);
                    layouts.Add(e.Elapsed.TotalMilliseconds + watch.Elapsed.TotalMilliseconds);
                };
                chat.Turns.CollectionChanged += (_, _) =>
                {
                    var last = chat.Turns.LastOrDefault();
                    host.Turn = last is { IsUser: false } ? last : null;
                };

                chat.InputText = "/long please";
                await chat.SendAsync().WithTimeout(TimeSpan.FromSeconds(60));
                var reply = chat.Turns.Last();
                Assert.Equal(ChatTurnState.Complete, reply.State);
                length = reply.Text.Length;
                Assert.Contains(reply.Markdown.Blocks, b => b.Kind == MarkdownBlockKind.Table);
                Assert.True(cache.Get(reply).Renderer.Blocks.Count > 20);
                await supervisor.ShutdownAsync(TimeSpan.FromSeconds(2));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("PILLION_FAKEHOST_MODE", null);
            Environment.SetEnvironmentVariable("PILLION_FAKEHOST_CHUNK_DELAY_MS", null);
            try
            {
                Directory.Delete(logDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        // The first refresh renders an empty message; the rest are streamed chunks.
        var chunks = refreshes.Skip(1).OrderBy(t => t).ToList();
        var withLayout = layouts.Skip(1).OrderBy(t => t).ToList();
        Assert.True(chunks.Count > 20, "refreshes: " + chunks.Count);
        var median = chunks[chunks.Count / 2];
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} refreshes for a {1}-character reply. Parse, plan and element update: median {2:0.000} ms, p95 {3:0.000} ms, max {4:0.000} ms, mean {5:0.000} ms. With layout: median {6:0.000} ms, p95 {7:0.000} ms, max {8:0.000} ms",
            chunks.Count,
            length,
            median,
            chunks[(int)(chunks.Count * 0.95)],
            chunks[chunks.Count - 1],
            chunks.Average(),
            withLayout[withLayout.Count / 2],
            withLayout[(int)(withLayout.Count * 0.95)],
            withLayout[withLayout.Count - 1]));

        // The target is 2 ms per chunk (the output above reports it); the assertion allows twice that for busy CI runners.
        Assert.True(median < 4.0, "median render per chunk " + median.ToString("0.000", CultureInfo.InvariantCulture) + " ms");
    }

    /// <summary>
    /// The virtualised transcript with 200 messages (100 questions, 100 Markdown replies with code blocks and a table):
    /// only the visible turns are realised, and each scroll step (layout included) is timed, first while replies render
    /// for the first time, then again once they are cached.
    /// </summary>
    [Fact]
    public void Transcript_200MessagesVirtualiseAndScrollSmoothly() => Sta.Run(() =>
    {
        var turns = new List<ChatTurnViewModel>();
        for (var i = 0; i < 100; i++)
        {
            turns.Add(ChatTurnViewModel.ForUser("Question " + i.ToString(CultureInfo.InvariantCulture)));
            turns.Add(Reply(
                "## Answer " + i.ToString(CultureInfo.InvariantCulture) + "\n\nSome **text** with `code` and a [link](https://example.com).\n\n"
                + "- a point\n- another point\n\n```csharp\npublic int Value" + i.ToString(CultureInfo.InvariantCulture) + " => 42;\n```\n\n"
                + (i % 4 == 0 ? "| A | B |\n| - | - |\n| 1 | 2 |\n\n" : string.Empty) + "Done."));
        }

        using var cache = new MessageRenderCache(Context([]));
        var transcript = Transcript(turns, cache);
        var size = new Size(420, 600);
        transcript.Measure(size);
        transcript.Arrange(new Rect(size));
        transcript.UpdateLayout();
        var scroller = Descendants(transcript).OfType<ScrollViewer>().First();
        var panel = Descendants(transcript).OfType<VirtualizingStackPanel>().Single();
        var realised = panel.Children.Count;
        Assert.InRange(realised, 1, 40);

        var cold = ScrollThrough(transcript, scroller, size);
        var renderedAfterFirstPass = cache.Count;
        Assert.Equal(turns.Count, renderedAfterFirstPass); // Every turn was rendered once, through the inherited cache.
        // Three cached passes, each step's best kept: a step the scheduler preempted (other test processes run beside this
        // one) is slow in one pass, a step that is really slow is slow in all three.
        var warmPasses = Enumerable.Range(0, 3).Select(_ => ScrollThrough(transcript, scroller, size)).ToList();
        var warm = Enumerable.Range(0, warmPasses.Min(p => p.Count)).Select(i => warmPasses.Min(p => p[i])).ToList();
        Assert.True(scroller.ExtentHeight > 20 * size.Height, "extent " + scroller.ExtentHeight);
        Assert.InRange(panel.Children.Count, 1, 40);
        Assert.Equal(renderedAfterFirstPass, cache.Count);

        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "200 messages, extent {0:0} px, {1} containers realised. Scroll step (120 px, layout included): first pass median {2:0.00} ms, p95 {3:0.00} ms, max {4:0.00} ms; cached passes (best of 3 per step) median {5:0.00} ms, p95 {6:0.00} ms, max {7:0.00} ms",
            scroller.ExtentHeight,
            realised,
            Percentile(cold, 0.5),
            Percentile(cold, 0.95),
            cold.Max(),
            Percentile(warm, 0.5),
            Percentile(warm, 0.95),
            warm.Max()));
        Assert.True(Percentile(warm, 0.95) < 16.7, "a cached scroll step fits in a 60 Hz frame: p95 " + Percentile(warm, 0.95).ToString("0.00", CultureInfo.InvariantCulture) + " ms");
    });

    private static string FakeHostPath => typeof(ChatRenderingTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "FakeHostPath").Value!;

    private static ChatTurnViewModel Reply(string text)
    {
        var turn = ChatTurnViewModel.ForAssistant(ModelOption.Build([TestModels.Local], ChatModelPolicy.Default).Single());
        turn.Append(text);
        return turn;
    }

    private static MarkdownRenderContext Context(List<Uri> opened) => new()
    {
        Theme = new MarkdownTheme { TextBrushKey = "Text", LinkBrushKey = "Link", BorderBrushKey = "Border", CodeBackgroundBrushKey = "CodeBackground" },
        OpenLink = opened.Add,
        BaseFontSize = () => 12,
    };

    /// <summary>The same virtualisation settings as the chat window's transcript (ChatToolWindowControl.xaml).</summary>
    private static ItemsControl Transcript(IReadOnlyList<ChatTurnViewModel> turns, MessageRenderCache cache)
    {
        var item = new FrameworkElementFactory(typeof(StackPanel));
        var header = new FrameworkElementFactory(typeof(TextBlock));
        header.SetBinding(TextBlock.TextProperty, new Binding(nameof(ChatTurnViewModel.Header)));
        item.AppendChild(header);
        var message = new FrameworkElementFactory(typeof(MarkdownMessageView));
        message.SetBinding(MarkdownMessageView.TurnProperty, new Binding());
        item.AppendChild(message);

        var scroller = new FrameworkElementFactory(typeof(ScrollViewer));
        scroller.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroller.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroller.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroller.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));

        var transcript = new ItemsControl
        {
            ItemsSource = turns,
            ItemTemplate = new DataTemplate { VisualTree = item },
            Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = scroller },
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
        };
        VirtualizingPanel.SetIsVirtualizing(transcript, true);
        VirtualizingPanel.SetVirtualizationMode(transcript, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(transcript, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLengthUnit(transcript, VirtualizationCacheLengthUnit.Page);
        VirtualizingPanel.SetCacheLength(transcript, new VirtualizationCacheLength(1, 1));
        ScrollViewer.SetCanContentScroll(transcript, true);
        MarkdownMessageView.SetCache(transcript, cache);
        return transcript;
    }

    private static List<double> ScrollThrough(ItemsControl transcript, ScrollViewer scroller, Size size)
    {
        var timings = new List<double>();
        var watch = new System.Diagnostics.Stopwatch();
        scroller.ScrollToTop();
        transcript.UpdateLayout();
        for (var step = 0; step < 2000 && scroller.VerticalOffset < scroller.ScrollableHeight - 1; step++)
        {
            watch.Restart();
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + 120);
            transcript.Measure(size);
            transcript.Arrange(new Rect(size));
            transcript.UpdateLayout();
            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        return timings;
    }

    private static double Percentile(List<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * percentile))];
    }

    private static IEnumerable<Inline> Flatten(FlowDocument document)
    {
        foreach (var block in document.Blocks.OfType<Paragraph>())
        {
            foreach (var inline in Flatten(block.Inlines))
            {
                yield return inline;
            }
        }
    }

    private static IEnumerable<Inline> Flatten(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
            {
                foreach (var child in Flatten(span.Inlines))
                {
                    yield return child;
                }
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        // Logical children cover elements that are not laid out yet; visual children cover templates.
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
            {
                stack.Push(child);
            }

            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                {
                    stack.Push(VisualTreeHelper.GetChild(current, i));
                }
            }
        }
    }

    /// <summary>Runs test code on an STA thread with a WPF dispatcher.</summary>
    private static class Sta
    {
        public static void Run(Action action) => RunOnDispatcher(_ =>
        {
            action();
            return Task.CompletedTask;
        });

        /// <summary>
        /// Runs async test code on a dispatcher thread whose <see cref="JoinableTaskContext"/> treats it as the main thread,
        /// as Visual Studio's UI thread is.
        /// </summary>
        public static void RunOnDispatcher(Func<JoinableTaskContext, Task> test)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                using var context = new JoinableTaskContext();

                // Bootstraps the dispatcher before any joinable task exists, so there is nothing to switch with yet.
#pragma warning disable VSTHRD001
                _ = dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await test(context);
                    }
#pragma warning disable CA1031 // Rethrown on the test thread.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        failure = ExceptionDispatchInfo.Capture(ex);
                    }
                    finally
                    {
                        dispatcher.InvokeShutdown();
                    }
                });
#pragma warning restore VSTHRD001
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromMinutes(3)))
            {
                throw new TimeoutException("The WPF test thread did not finish.");
            }

            failure?.Throw();
        }
    }
}
