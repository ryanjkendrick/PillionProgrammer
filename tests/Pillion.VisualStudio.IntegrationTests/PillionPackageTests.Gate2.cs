using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// The Gate 2 scenarios (work item 2.17), run by <c>tools/gate2/Invoke-Gate2.ps1</c> in the Experimental Instance with the
/// real Pillion Host, the bundled embedding model and Ollama; skipped otherwise (<see cref="Gate2ScenarioFactAttribute"/>).
/// They come after the Gate 1 scenarios (which then run with retrieval on) and use two workspaces the script prepares:
/// <c>%PILLION_GATE2_SOLUTION%</c>, a generated solution of about 100,000 lines in a git repository with a realistic-diff and
/// a whole-file-rewrite branch of 500 files and two secret-bearing files; and <c>%PILLION_GATE2_BIG_REPO%</c>, a generated
/// folder of about 1,000,000 lines whose index (about 120,000 chunks) step d of the script built in the data folder. Each
/// writes its figures to <c>%PILLION_GATE_OUT%</c> as <c>gate2-&lt;name&gt;-VS&lt;major&gt;.json</c>. Waits that can take minutes
/// use their own token, not the framework's hang token.
/// </summary>
public sealed partial class PillionPackageTests
{
    /// <summary>A questions-and-identifiers mix for the search and assembly measurements on the generated code.</summary>
    private static readonly string[] Gate2Queries =
    [
        "where is the item count kept", "how is a service disposed", "Service12_3", "Service407_1", "count the items in the list",
        "which method adds an item", "Generated.Module4", "dispose the items", "Service901_0", "what does Count return",
        "how are the totals computed", "Service55_2", "remove an item from the list", "IDisposable implementation", "Service250_4",
        "where are items cleared", "which services call each other", "Service700_5", "add a range of values", "Service33_0",
    ];

    /// <summary>The branch files whose new identifiers are searched for after a checkout.</summary>
    private static readonly int[] BranchSampleFiles = [0, 123, 250, 377, 499];

    private static string Gate2Solution => Environment.GetEnvironmentVariable("PILLION_GATE2_SOLUTION") ?? string.Empty;

    private static string Gate2BigRepo => Environment.GetEnvironmentVariable("PILLION_GATE2_BIG_REPO") ?? string.Empty;

    /// <summary>The generated solution is open, indexed by the real host; secret-bearing files are excluded, listed and need approval.</summary>
    [Gate2ScenarioFact]
    [TestOrder(40)]
    public async Task Gate2_GeneratedSolution_IsIndexed_AndSecretFilesAreExcludedListedAndNeedApproval()
    {
        using var lease = Gate2Lease(TimeSpan.FromMinutes(20));
        var clock = Stopwatch.StartNew();
        await OpenGate2SolutionAsync(lease.Token);
        var opened = clock.Elapsed;
        var (status, root) = await WaitForIndexedRootAsync(Gate2Folder, TimeSpan.FromMinutes(15), lease.Token);
        var indexed = clock.Elapsed;
        var context = await Gate2ContextAsync();

        var secret = await context.ListExcludedFilesAsync(new ExcludedFilesRequest { RootPath = root.RootPath, SecretOnly = true, Limit = 50 }, lease.Token);
        var all = await context.ListExcludedFilesAsync(new ExcludedFilesRequest { RootPath = root.RootPath, Limit = 200 }, lease.Token);
        var env = Path.Combine(Gate2Folder, ".env");
        var unapproved = await context.ResolveMentionAsync(new MentionRequest { Mention = ".env", Kind = MentionKinds.File, WorkspaceRoots = [Gate2Folder], CandidateFiles = [env], MaxTokens = 2000 }, lease.Token);
        var approved = await context.ResolveMentionAsync(new MentionRequest { Mention = ".env", Kind = MentionKinds.File, WorkspaceRoots = [Gate2Folder], CandidateFiles = [env], MaxTokens = 2000, ApprovedSensitive = true }, lease.Token);
        var envHits = await context.SearchDetailedAsync(new SearchRequest { Query = "API_KEY DB_PASSWORD", Mode = SearchMode.Lexical, MaxResults = 10, Roots = [root.RootPath] }, lease.Token);

        var result = new JObject
        {
            ["solution"] = Gate2Solution,
            ["openSeconds"] = Math.Round(opened.TotalSeconds, 1),
            ["indexedSeconds"] = Math.Round((indexed - opened).TotalSeconds, 1),
            ["root"] = Describe(root),
            ["phase"] = status.Phase.ToString(),
            ["secretFiles"] = new JArray(secret.Items.Select(i => $"{i.Path} ({i.Reason})")),
            ["excludedTotal"] = all.TotalCount,
            ["excludedFirst"] = new JArray(all.Items.Take(10).Select(i => $"{i.Path} ({i.Reason}{(i.IsSecretBearing ? ", secret" : string.Empty)})")),
            ["envMention"] = new JObject { ["resolved"] = unapproved.IsResolved, ["requiresApproval"] = unapproved.RequiresApproval, ["isSensitive"] = unapproved.IsSensitive, ["hasContent"] = !string.IsNullOrEmpty(unapproved.Content), ["reason"] = unapproved.Reason },
            ["envMentionApproved"] = new JObject { ["resolved"] = approved.IsResolved, ["hasContent"] = !string.IsNullOrEmpty(approved.Content), ["tokens"] = approved.TokenCount },
            ["secretTextSearchHits"] = new JArray(envHits.Results.Select(r => r.FilePath)),
        };
        await WriteGate2Async("index-solution", result);

        Assert.True(root.ChunkCount > 5000, "The generated solution was not indexed: " + Describe(root));
        Assert.Contains(secret.Items, i => i.Path.EndsWith(".env", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(secret.Items, i => i.Path.EndsWith("appsettings.Development.json", StringComparison.OrdinalIgnoreCase));
        Assert.True(unapproved.RequiresApproval || unapproved.IsSensitive, ".env resolved without asking for approval: " + unapproved.Reason);
        Assert.True(string.IsNullOrEmpty(unapproved.Content), ".env content came back without approval.");
        Assert.DoesNotContain(envHits.Results, r => r.FilePath.EndsWith(".env", StringComparison.OrdinalIgnoreCase) || r.FilePath.EndsWith("appsettings.Development.json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Keystroke latency (TypeChar on the UI thread, autocomplete off) while the index is being rebuilt the whole time.</summary>
    [Gate2ScenarioFact]
    [TestOrder(41)]
    public Task Gate2_Keystrokes_WhileIndexing() => Gate2TypingPassAsync("keystrokes-indexing", "TypeIndexing.cs", keepIndexing: true);

    /// <summary>The same keystrokes with the index idle, for the difference.</summary>
    [Gate2ScenarioFact]
    [TestOrder(42)]
    public Task Gate2_Keystrokes_IndexIdle() => Gate2TypingPassAsync("keystrokes-idle", "TypeIdle.cs", keepIndexing: false);

    /// <summary>One saved file becomes searchable: ten one-member edits and three whole-file rewrites saved through Visual Studio.</summary>
    [Gate2ScenarioFact]
    [TestOrder(43)]
    public async Task Gate2_SavedFile_BecomesSearchable()
    {
        using var lease = Gate2Lease(TimeSpan.FromMinutes(10));
        await OpenGate2SolutionAsync(lease.Token);
        var (_, root) = await WaitForIndexedRootAsync(Gate2Folder, TimeSpan.FromMinutes(10), lease.Token);
        var context = await Gate2ContextAsync();
        var edits = new JArray();
        var rewrites = new JArray();
        var editTimes = new List<double>();
        var rewriteTimes = new List<double>();
        var stamp = DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
        for (var i = 0; i < 13; i++)
        {
            var rewrite = i >= 10;
            var index = 520 + i; // Files the branches do not touch.
            var path = Path.Combine(Gate2Folder, "src", "Module" + (index % 12).ToString(CultureInfo.InvariantCulture), "File" + index.ToString(CultureInfo.InvariantCulture) + ".cs");
            var marker = (rewrite ? "SaveRewrite" : "SaveEdit") + stamp + "x" + i.ToString(CultureInfo.InvariantCulture);
            var document = await OpenDteDocumentAsync(path, lease.Token);
            await JoinableTaskFactory.SwitchToMainThreadAsync(lease.Token);
            var text = (TextDocument)document.Object("TextDocument");
            if (rewrite)
            {
                // Every member renamed: all chunks new (the partial-vector path of the save budget).
                var all = text.StartPoint.CreateEditPoint().GetText(text.EndPoint);
                var renamed = all.Replace("Process" + index.ToString(CultureInfo.InvariantCulture) + "_", marker + "_");
                text.StartPoint.CreateEditPoint().ReplaceText(text.EndPoint, renamed, (int)vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers);
            }
            else
            {
                // One new member after the fields (line 12).
                var point = text.StartPoint.CreateEditPoint();
                point.LineDown(11);
                point.Insert("        public int " + marker + "(int value) => value + " + i.ToString(CultureInfo.InvariantCulture) + ";" + Environment.NewLine + Environment.NewLine);
            }

            var query = rewrite ? marker + "_3" : marker;
            var clock = Stopwatch.StartNew();
            document.Save();
            var saved = clock.Elapsed;
            var found = await WaitUntilSearchableAsync(context, root.RootPath, query, path, TimeSpan.FromSeconds(30), lease.Token);
            var entry = new JObject { ["file"] = Path.GetFileName(path), ["marker"] = query, ["saveCallMs"] = Math.Round(saved.TotalMilliseconds, 1), ["searchableMs"] = found is null ? null : Math.Round(found.Value.TotalMilliseconds, 1) };
            (rewrite ? rewrites : edits).Add(entry);
            if (found is { } f)
            {
                (rewrite ? rewriteTimes : editTimes).Add(f.TotalMilliseconds);
            }

            Output.WriteLine(entry.ToString(Newtonsoft.Json.Formatting.None));
            await JoinableTaskFactory.SwitchToMainThreadAsync(lease.Token);
            document.Close(vsSaveChanges.vsSaveChangesNo);
            await Task.Delay(1500, lease.Token); // Let the rest of the vectors of a large save land before the next one.
        }

        var result = new JObject
        {
            ["edits"] = edits,
            ["editsSearchable"] = GateScenarioChecks.Percentiles(editTimes),
            ["rewrites"] = rewrites,
            ["rewritesSearchable"] = GateScenarioChecks.Percentiles(rewriteTimes),
            ["how"] = "DTE Document.Save of an edited file (documentSaved to the host), then SearchDetailedAsync (lexical) every 10 ms until the saved file's new identifier is found; clock from just before Save",
        };
        await WriteGate2Async("save-searchable", result);
        Assert.Equal(10, editTimes.Count);
    }

    /// <summary>A scripted branch switch touching 500 files: two members changed in each (the realistic diff).</summary>
    [Gate2ScenarioFact]
    [TestOrder(44)]
    public Task Gate2_BranchSwitch_RealisticDiff() => Gate2BranchSwitchAsync("realistic", "BranchMarker{0}_1");

    /// <summary>The same 500 files rewritten whole (every chunk new).</summary>
    [Gate2ScenarioFact]
    [TestOrder(45)]
    public Task Gate2_BranchSwitch_WholeFileRewrite() => Gate2BranchSwitchAsync("rewrite", "RewriteMarker{0}_0");

    /// <summary>Workspace queries answered by devenv.exe: @symbol resolutions through the host, which asks the live Roslyn workspace.</summary>
    [Gate2ScenarioFact]
    [TestOrder(46)]
    public async Task Gate2_WorkspaceQueries_ThroughSymbolMentions()
    {
        using var lease = Gate2Lease(TimeSpan.FromMinutes(8));
        await OpenGate2SolutionAsync(lease.Token);
        await WaitForIndexedRootAsync(Gate2Folder, TimeSpan.FromMinutes(5), lease.Token);
        var context = await Gate2ContextAsync();
        var times = new List<double>();
        var samples = new JArray();
        for (var i = 0; i < 24; i++)
        {
            var index = (i * 37) % 600;
            var name = "Process" + index.ToString(CultureInfo.InvariantCulture) + "_2";
            var clock = Stopwatch.StartNew();
            var resolution = await context.ResolveMentionAsync(new MentionRequest { Mention = name, Kind = MentionKinds.Symbol, WorkspaceRoots = [Gate2Folder], MaxTokens = 1500 }, lease.Token);
            clock.Stop();
            if (i >= 4)
            {
                times.Add(clock.Elapsed.TotalMilliseconds);
            }

            samples.Add($"{name}: {clock.Elapsed.TotalMilliseconds:0} ms, resolved {resolution.IsResolved}, {resolution.TokenCount} tokens");
            await Task.Delay(250, lease.Token); // Under the five-a-second limit.
        }

        var result = new JObject
        {
            ["symbolMentions"] = GateScenarioChecks.Percentiles(times),
            ["samples"] = samples,
            ["how"] = "IContextService.ResolveMentionAsync(@symbol) from devenv.exe: the host asks the VSIX's IWorkspaceQueryService (Roslyn, off the UI thread) and falls back to the index; 4 warm-ups, 20 measured, 250 ms apart",
        };
        await WriteGate2Async("workspace-queries", result);
    }

    /// <summary>A conversation, its pins and the Outbound log survive a host kill; the conversation id is kept for the restart check.</summary>
    [Gate2ScenarioFact]
    [TestOrder(47)]
    public async Task Gate2_Persistence_SurvivesAHostKill()
    {
        using var lease = Gate2Lease(TimeSpan.FromMinutes(8));
        await OpenGate2SolutionAsync(lease.Token);
        var connection = await GateScenarioChecks.ConnectionAsync();
        var conversations = connection.GetProxy<IConversationService>();
        var id = Guid.NewGuid().ToString("N");
        var title = "Gate 2 persistence " + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        await conversations.CreateConversationAsync(new ConversationCreateRequest { Id = id, Title = title }, lease.Token);
        await conversations.SetPinsAsync(id, [new ConversationPin { Kind = MentionKinds.File, Target = Path.Combine(Gate2Folder, "Program.cs"), DisplayName = "Program.cs", TokenCount = 40 }], lease.Token);
        var requestId = Guid.NewGuid().ToString("N");
        var reply = await ChatOnceAsync(connection, requestId, id, "In one short sentence: what does Program.Main do?", lease.Token);
        var outboundBefore = (await connection.GetProxy<IDiagnosticsService>().GetOutboundRecordsAsync(500, lease.Token)).Count(r => r.RequestId == requestId);

        var restart = await PillionHostChecks.KillHostAndWaitForReadyAsync(TimeSpan.FromSeconds(30), lease.Token);
        connection = await GateScenarioChecks.ConnectionAsync();
        var detail = await connection.GetProxy<IConversationService>().GetConversationAsync(id, lease.Token);
        var outboundAfter = (await connection.GetProxy<IDiagnosticsService>().GetOutboundRecordsAsync(500, lease.Token)).Count(r => r.RequestId == requestId);

        var result = new JObject
        {
            ["conversationId"] = id,
            ["title"] = title,
            ["reply"] = reply.Text.Length > 200 ? reply.Text.Substring(0, 200) : reply.Text,
            ["hostRestart"] = restart.ToString(),
            ["afterKill"] = new JObject
            {
                ["found"] = detail is not null,
                ["messages"] = detail?.Messages.Length ?? 0,
                ["pins"] = detail?.Pins.Length ?? 0,
                ["outboundRecordsOfTheRequest"] = outboundAfter,
            },
            ["outboundRecordsBefore"] = outboundBefore,
        };
        await WriteGate2Async("persistence-host", result);
        Assert.NotNull(detail);
        Assert.True(detail!.Messages.Length >= 2, $"{detail.Messages.Length} messages after the host kill.");
        Assert.Single(detail.Pins);
        Assert.True(outboundAfter >= 1, "The Outbound record of the request did not survive the host kill.");
    }

    /// <summary>Semantic search on the 100,000-chunk index through SearchDetailedAsync: p50 and p95 from devenv.exe.</summary>
    [Gate2ScenarioFact]
    [TestOrder(48)]
    public async Task Gate2_BigIndex_SearchLatency()
    {
        using var lease = Gate2Lease(TimeSpan.FromMinutes(25));
        var (root, opened) = await OpenBigRepoAsync(lease.Token);
        var context = await Gate2ContextAsync();
        var client = new List<double>();
        var server = new List<double>();
        var degraded = 0;
        var empty = 0;
        for (var i = 0; i < 3 + 40; i++)
        {
            var query = Gate2Queries[i % Gate2Queries.Length];
            var clock = Stopwatch.StartNew();
            var response = await context.SearchDetailedAsync(new SearchRequest { Query = query, Mode = SearchMode.Hybrid, MaxResults = 10, Roots = [root.RootPath] }, lease.Token);
            clock.Stop();
            if (i < 3)
            {
                continue;
            }

            client.Add(clock.Elapsed.TotalMilliseconds);
            server.Add(response.TotalMs);
            degraded += response.Degraded ? 1 : 0;
            empty += response.Results.Length == 0 ? 1 : 0;
        }

        var result = new JObject
        {
            ["root"] = Describe(root),
            ["openedToIdleSeconds"] = Math.Round(opened.TotalSeconds, 1),
            ["client"] = GateScenarioChecks.Percentiles(client),
            ["host"] = GateScenarioChecks.Percentiles(server),
            ["degraded"] = degraded,
            ["empty"] = empty,
            ["how"] = "IContextService.SearchDetailedAsync, hybrid, top 10, no reranker, 20 questions and identifiers twice after 3 warm-ups; client = round trip from devenv.exe, host = SearchResponse.TotalMs",
        };
        await WriteGate2Async("search", result);
        Assert.True(root.ChunkCount >= 90_000, "The big index holds fewer than 90,000 chunks: " + Describe(root));
    }

    /// <summary>Context assembly with retrieval on the same index: the Context Inspector's preview, p50 and p95.</summary>
    [Gate2ScenarioFact]
    [TestOrder(49)]
    public async Task Gate2_BigIndex_ContextAssemblyWithRetrieval()
    {
        using var lease = Gate2Lease(TimeSpan.FromMinutes(25));
        var (root, _) = await OpenBigRepoAsync(lease.Token);
        var connection = await GateScenarioChecks.ConnectionAsync();
        var diagnostics = connection.GetProxy<IDiagnosticsService>();
        var times = new List<double>();
        var retrieved = new List<int>();
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var currentFile = Directory.EnumerateFiles(root.RootPath, "*.cs", SearchOption.AllDirectories).First();
        for (var i = 0; i < 3 + 40; i++)
        {
            var request = new ChatRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Messages = [new ChatMessage { Role = ChatRole.User, Content = Gate2Queries[i % Gate2Queries.Length] + "?" }],
                Profile = Profile.LocalStrong,
                CurrentFile = currentFile,
                RetrievalMode = RetrievalMode.On,
                MaxRetrievedChunks = 8,
                ConversationId = Guid.NewGuid().ToString("N"),
            };
            var clock = Stopwatch.StartNew();
            var report = await diagnostics.PreviewContextAsync(request, lease.Token);
            clock.Stop();
            if (i < 3)
            {
                continue;
            }

            times.Add(clock.Elapsed.TotalMilliseconds);
            foreach (var item in report.Items)
            {
                kinds.Add(item.Kind);
            }

            retrieved.Add(report.Items.Count(it => it.Kind.IndexOf("retriev", StringComparison.OrdinalIgnoreCase) >= 0 || it.Kind.IndexOf("chunk", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        var result = new JObject
        {
            ["root"] = Describe(root),
            ["assembly"] = GateScenarioChecks.Percentiles(times),
            ["retrievedItemsPerRequest"] = retrieved.Count == 0 ? null : Math.Round(retrieved.Average(), 1),
            ["itemKinds"] = new JArray(kinds.OrderBy(k => k, StringComparer.Ordinal)),
            ["how"] = "IDiagnosticsService.PreviewContextAsync (the Context Inspector: assembled for the model chat routes to, retrieval on, 8 chunks and the repo map, no model call) from devenv.exe; 3 warm-ups, 40 measured",
        };
        await WriteGate2Async("context-assembly", result);
    }

    /// <summary>A chat on the indexed code: the reply cites file:line spans of retrieved chunks.</summary>
    [Gate2ScenarioFact]
    [TestOrder(50)]
    public async Task Gate2_ChatWithRetrieval_CitesSpans()
    {
        using var lease = Gate2Lease(TimeSpan.FromMinutes(10));
        await OpenGate2SolutionAsync(lease.Token);
        var (_, root) = await WaitForIndexedRootAsync(Gate2Folder, TimeSpan.FromMinutes(5), lease.Token);
        var connection = await GateScenarioChecks.ConnectionAsync();
        var reply = await ChatOnceAsync(connection, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "Where is the running total stored after a step is processed? Name the file and line.", lease.Token);
        var cited = reply.Citations;
        var result = new JObject
        {
            ["reply"] = reply.Text.Length > 600 ? reply.Text.Substring(0, 600) : reply.Text,
            ["model"] = reply.Model,
            ["citations"] = new JArray(cited.Select(c => $"{c.FilePath}:{c.StartLine}-{c.EndLine} (offset {c.ReplyOffset})")),
            ["explicit"] = cited.Count(c => c.ReplyOffset >= 0),
            ["implicit"] = cited.Count(c => c.ReplyOffset < 0),
            ["insideRoot"] = cited.All(c => c.FilePath.StartsWith(root.RootPath, StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(c.FilePath)),
            ["error"] = reply.Error,
        };
        await WriteGate2Async("chat-cites", result);
        Assert.True(cited.Length > 0, "The reply cites nothing: " + reply.Text);
    }

    /// <summary>The Index window lists the real host's roots with their counts.</summary>
    [Gate2ScenarioFact]
    [TestOrder(51)]
    public async Task Gate2_IndexWindow_ShowsTheRealRoot()
    {
        using var lease = Gate2Lease(TimeSpan.FromMinutes(6));
        await OpenGate2SolutionAsync(lease.Token);
        var (_, root) = await WaitForIndexedRootAsync(Gate2Folder, TimeSpan.FromMinutes(5), lease.Token);
        await TestServices.Shell.ExecuteCommandAsync("Tools.PillionOpenIndex", lease.Token);
        IVsWindowFrame? frame = null;
        await WaitUntilAsync(
            async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(lease.Token);
                var shell = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsUIShell, IVsUIShell>(lease.Token);
                var guid = IndexToolWindowGuid;
                frame = ErrorHandler.Succeeded(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fFindFirst, ref guid, out var found)) ? found : null;
                return frame is not null && frame.IsVisible() == VSConstants.S_OK;
            },
            TimeSpan.FromSeconds(30),
            "the Pillion Index window to be shown");
        await JoinableTaskFactory.SwitchToMainThreadAsync(lease.Token);
        ErrorHandler.ThrowOnFailure(frame!.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var view));
        var control = (view as ToolWindowPane)?.Content as FrameworkElement ?? throw new InvalidOperationException("The Pillion Index window has no content.");
        var vm = control.DataContext as IndexWindowViewModel ?? throw new InvalidOperationException("The Index window's DataContext is not an IndexWindowViewModel.");
        var deadline = Stopwatch.StartNew();
        while (!vm.Roots.Any(r => string.Equals(r.RootPath, root.RootPath, StringComparison.OrdinalIgnoreCase)) && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(250, lease.Token);
            await JoinableTaskFactory.SwitchToMainThreadAsync(lease.Token);
        }

        var rows = new JArray(vm.Roots.Select(r => $"{r.RootPath}; {r.RoleText}; files {r.FilesText}; chunks {r.ChunksText}; {r.DiskText}; {r.ModelText}; last sweep {r.LastSweepText}"));
        var result = new JObject
        {
            ["phase"] = vm.PhaseText,
            ["summary"] = vm.SummaryText,
            ["rows"] = rows,
            ["excludedTotal"] = vm.Excluded.TotalCount,
            ["hostRoot"] = Describe(root),
        };
        _ = frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        await WriteGate2Async("index-window", result);
        Assert.Contains(vm.Roots, r => string.Equals(r.RootPath, root.RootPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>In a second Visual Studio session: the conversation written before the restart is still there.</summary>
    [Gate2RestartFact]
    [TestOrder(60)]
    public async Task Gate2_Persistence_SurvivesAVisualStudioRestart()
    {
        using var lease = Gate2Lease(TimeSpan.FromMinutes(5));
        var earlier = Path.Combine(GateOutput, $"gate2-persistence-host-VS{(await GetVisualStudioVersionAsync()).Major}.json");
        Assert.True(File.Exists(earlier), "No result of the host-kill persistence scenario at " + earlier);
        var id = (string)JObject.Parse(File.ReadAllText(earlier))["conversationId"]!;
        await EnsurePackageLoadedAsync();
        await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(60), lease.Token);
        var connection = await GateScenarioChecks.ConnectionAsync();
        var detail = await connection.GetProxy<IConversationService>().GetConversationAsync(id, lease.Token);
        var list = await connection.GetProxy<IConversationService>().ListConversationsAsync(new ConversationListRequest { Limit = 50 }, lease.Token);
        var result = new JObject
        {
            ["conversationId"] = id,
            ["found"] = detail is not null,
            ["messages"] = detail?.Messages.Length ?? 0,
            ["pins"] = detail?.Pins.Length ?? 0,
            ["listed"] = list.Items.Any(c => c.Id == id),
        };
        await WriteGate2Async("persistence-restart", result);
        Assert.NotNull(detail);
        Assert.True(detail!.Messages.Length >= 2, $"{detail.Messages.Length} messages after the restart.");
    }

    private static string Gate2Folder => Path.GetDirectoryName(Gate2Solution) ?? string.Empty;

    private static CancellationTokenSource Gate2Lease(TimeSpan timeout) => new(timeout);

    private static JObject Describe(IndexRootStatus root) => new()
    {
        ["rootPath"] = root.RootPath,
        ["role"] = root.Role.ToString(),
        ["files"] = root.IndexedFileCount,
        ["totalFiles"] = root.TotalFileCount,
        ["chunks"] = root.ChunkCount,
        ["diskBytes"] = root.DiskBytes,
        ["bytesPerChunk"] = root.ChunkCount == 0 ? null : Math.Round(root.DiskBytes / (double)root.ChunkCount),
        ["excluded"] = root.ExcludedFileCount,
        ["secret"] = root.SecretFileCount,
        ["model"] = root.EmbeddingModelId,
        ["chunker"] = root.ChunkerVersion,
        ["lastFullSweepUtc"] = root.LastFullSweepUtc?.ToString("o", CultureInfo.InvariantCulture),
    };

    private static async Task<IContextService> Gate2ContextAsync() => (await GateScenarioChecks.ConnectionAsync()).GetProxy<IContextService>();

    private async Task WriteGate2Async(string name, JObject result)
    {
        var version = await GetVisualStudioVersionAsync();
        result["visualStudio"] = version.ToString();
        result["writtenUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        Output.WriteLine($"{name}: {result.ToString(Newtonsoft.Json.Formatting.None)}");
        Output.WriteLine("Wrote " + GateScenarioChecks.WriteResult(GateOutput, $"gate2-{name}-VS{version.Major}", result));
    }

    /// <summary>Opens the generated solution unless it is the open one, and waits for the package and the host.</summary>
    private async Task OpenGate2SolutionAsync(CancellationToken cancellationToken)
    {
        Assert.True(File.Exists(Gate2Solution), "PILLION_GATE2_SOLUTION does not name a solution: " + Gate2Solution);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var dte = await TestServices.Shell.GetRequiredGlobalServiceAsync<SDTE, DTE>(cancellationToken);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (!string.Equals(dte.Solution?.FullName, Gate2Solution, StringComparison.OrdinalIgnoreCase))
        {
            if (await TestServices.SolutionExplorer.IsSolutionOpenAsync(cancellationToken))
            {
                await TestServices.SolutionExplorer.CloseSolutionAsync(cancellationToken);
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            dte.Solution!.Open(Gate2Solution);
            await WaitUntilAsync(
                async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    var vsSolution = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsSolution, IVsSolution>(cancellationToken);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    return ErrorHandler.Succeeded(vsSolution.GetProperty((int)__VSPROPID4.VSPROPID_IsSolutionFullyLoaded, out var loaded)) && loaded is true && dte.Solution.Projects.Count >= 1;
                },
                TimeSpan.FromSeconds(120),
                "the generated solution to load");
            Output.WriteLine("Opened " + Gate2Solution);
        }

        await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
        await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(60), cancellationToken);
    }

    /// <summary>Opens the big generated folder (its index was built by step d) and waits until the host has it open and idle.</summary>
    private async Task<(IndexRootStatus Root, TimeSpan Opened)> OpenBigRepoAsync(CancellationToken cancellationToken)
    {
        Assert.True(Directory.Exists(Gate2BigRepo), "PILLION_GATE2_BIG_REPO does not name a folder: " + Gate2BigRepo);
        var clock = Stopwatch.StartNew();
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var dte = await TestServices.Shell.GetRequiredGlobalServiceAsync<SDTE, DTE>(cancellationToken);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var context = await Gate2ContextAsync();
        var status = await context.GetIndexStatusAsync(cancellationToken);
        if (!status.Roots.Any(r => Gate2SamePath(r.RootPath, Gate2BigRepo)))
        {
            if (await TestServices.SolutionExplorer.IsSolutionOpenAsync(cancellationToken))
            {
                await TestServices.SolutionExplorer.CloseSolutionAsync(cancellationToken);
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var solution = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsSolution, IVsSolution7>(cancellationToken);
            solution.OpenFolder(Gate2BigRepo);
            await WaitUntilAsync(() => TestServices.SolutionExplorer.IsSolutionOpenAsync(cancellationToken), TimeSpan.FromSeconds(120), "the big folder to open");
            await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
            await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }

        var (_, root) = await WaitForIndexedRootAsync(Gate2BigRepo, TimeSpan.FromMinutes(20), cancellationToken);
        return (root, clock.Elapsed);
    }

    private static bool Gate2SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Waits until the host owns a root at (or above) the folder, swept and idle.</summary>
    private static async Task<(IndexStatus Status, IndexRootStatus Root)> WaitForIndexedRootAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var context = await Gate2ContextAsync();
        var clock = Stopwatch.StartNew();
        IndexStatus? status = null;
        while (clock.Elapsed < timeout)
        {
            status = await context.GetIndexStatusAsync(cancellationToken);
            var root = status.Roots.FirstOrDefault(r => Gate2SamePath(r.RootPath, folder) || folder.StartsWith(r.RootPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (root is not null && root.Role == IndexRole.Writer && root.LastFullSweepUtc is not null && status.Phase == IndexPhase.Idle && !status.IsIndexing)
            {
                return (status, root);
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException($"The host did not index {folder} within {timeout.TotalMinutes:0} minutes: phase {status?.Phase}, paused {status?.IsPaused} ({status?.PauseReason}), roots {string.Join("; ", status?.Roots.Select(r => $"{r.RootPath} {r.Role} {r.ChunkCount} chunks {r.IndexedFileCount}/{r.TotalFileCount}") ?? [])}, error {status?.LastError}.");
    }

    /// <summary>Polls a lexical search every 10 ms until a result in the file holds the identifier; the time it took, or null.</summary>
    private static async Task<TimeSpan?> WaitUntilSearchableAsync(IContextService context, string root, string identifier, string file, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            var response = await context.SearchDetailedAsync(new SearchRequest { Query = identifier, Mode = SearchMode.Lexical, MaxResults = 5, Roots = [root], IncludeText = true }, cancellationToken).ConfigureAwait(false);
            if (response.Results.Any(r => (file.Length == 0 || Gate2SamePath(r.FilePath, file) || file.EndsWith(r.FilePath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
                && ((r.Text ?? string.Empty).Contains(identifier) || r.Snippet.Contains(identifier) || r.Breadcrumb.Contains(identifier))))
            {
                return clock.Elapsed;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<Document> OpenDteDocumentAsync(string path, CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var dte = await TestServices.Shell.GetRequiredGlobalServiceAsync<SDTE, DTE>(cancellationToken);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var window = dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
        return window.Document;
    }

    /// <summary>One typing pass with autocomplete off; with <paramref name="keepIndexing"/> the root is rebuilt whenever it goes idle, so indexing runs throughout.</summary>
    private async Task Gate2TypingPassAsync(string name, string file, bool keepIndexing)
    {
        using var lease = Gate2Lease(TimeSpan.FromMinutes(12));
        await OpenGate2SolutionAsync(lease.Token);
        var (_, root) = await WaitForIndexedRootAsync(Gate2Folder, TimeSpan.FromMinutes(10), lease.Token);
        var context = await Gate2ContextAsync();
        var feature = await GateScenarioChecks.GetAutocompleteFeatureAsync(TimeSpan.FromSeconds(30), lease.Token);
        var configured = await GateScenarioChecks.ConfigureAutocompleteAsync(feature, ProviderId, null, enabled: false, lease.Token);
        Output.WriteLine("Settings: " + configured);
        var path = Path.Combine(Gate2Folder, "typing", file);
        var busySamples = 0;
        var samples = 0;
        var rebuilds = 0;
        using var stop = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var status = await context.GetIndexStatusAsync(stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref samples);
                    if (status.IsIndexing || status.Phase != IndexPhase.Idle)
                    {
                        Interlocked.Increment(ref busySamples);
                    }
                    else if (keepIndexing)
                    {
                        await context.RebuildIndexAsync(root.RootPath, stop.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref rebuilds);
                    }

                    await Task.Delay(500, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        });

        JObject typing;
        try
        {
            if (keepIndexing)
            {
                // Indexing is running before the first keystroke.
                await context.RebuildIndexAsync(root.RootPath, lease.Token);
                Interlocked.Increment(ref rebuilds);
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(30) && (await context.GetIndexStatusAsync(lease.Token)).Phase == IndexPhase.Idle)
                {
                    await Task.Delay(100, lease.Token);
                }
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync(lease.Token);
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
            Microsoft.VisualStudio.Text.Editor.IWpfTextView? opened = null;
            await WaitUntilAsync(
                async () =>
                {
                    opened = await TestServices.Editor.GetActiveTextViewAsync(lease.Token);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(lease.Token);
                    _ = opened.VisualElement.Focus();
                    return opened.TextBuffer.Properties.TryGetProperty(typeof(Microsoft.VisualStudio.Text.ITextDocument), out Microsoft.VisualStudio.Text.ITextDocument document)
                        && Gate2SamePath(document.FilePath, path) && opened.HasAggregateFocus;
                },
                TimeSpan.FromSeconds(30),
                file + " to open with keyboard focus");
            var view = opened!;
            await JoinableTaskFactory.SwitchToMainThreadAsync(lease.Token);
            GateScenarioChecks.PrepareEditor(view);
            typing = await GateScenarioChecks.TypeSnippetAsync(view, new TypingOptions(CharacterDelay, TimeSpan.FromMilliseconds(400), AcceptMatchingProposals: false), () => Task.CompletedTask, lease.Token);
        }
        finally
        {
            stop.Cancel();
            await watcher;
            await JoinableTaskFactory.SwitchToMainThreadAsync(lease.Token);
            if (VsShellUtilities.IsDocumentOpen(ServiceProvider.GlobalProvider, path, Guid.Empty, out _, out _, out var frame) && frame is not null)
            {
                _ = frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
            }

            Output.WriteLine("Settings: " + await GateScenarioChecks.ConfigureAutocompleteAsync(feature, ProviderId, null, enabled: true, lease.Token));
        }

        if (keepIndexing)
        {
            // Leave the index complete for the scenarios that follow.
            await WaitForIndexedRootAsync(Gate2Folder, TimeSpan.FromMinutes(10), lease.Token);
        }

        var result = new JObject
        {
            ["indexingDuringPass"] = keepIndexing,
            ["statusSamples"] = samples,
            ["busySamples"] = busySamples,
            ["busyFraction"] = samples == 0 ? null : Math.Round(busySamples / (double)samples, 3),
            ["rebuilds"] = rebuilds,
            ["typing"] = typing,
            ["root"] = Describe(root),
        };
        await WriteGate2Async(name, result);
        if (keepIndexing)
        {
            Assert.True(samples > 0 && busySamples / (double)samples > 0.8, $"Indexing ran in only {busySamples} of {samples} status samples during the pass.");
        }
    }

    /// <summary>Checks out a branch of the generated solution (500 files changed) and measures until its markers are searchable and the host is idle; then checks master out again.</summary>
    private async Task Gate2BranchSwitchAsync(string branch, string markerFormat)
    {
        using var lease = Gate2Lease(TimeSpan.FromMinutes(15));
        await OpenGate2SolutionAsync(lease.Token);
        var (_, root) = await WaitForIndexedRootAsync(Gate2Folder, TimeSpan.FromMinutes(10), lease.Token);
        var context = await Gate2ContextAsync();
        await JoinableTaskFactory.SwitchToMainThreadAsync(lease.Token);
        var dte = await TestServices.Shell.GetRequiredGlobalServiceAsync<SDTE, DTE>(lease.Token);
        await JoinableTaskFactory.SwitchToMainThreadAsync(lease.Token);
        dte.Documents.CloseAll(vsSaveChanges.vsSaveChangesNo);
        var markers = BranchSampleFiles.Select(i => (Marker: string.Format(CultureInfo.InvariantCulture, markerFormat, i), File: Path.Combine(Gate2Folder, "src", "Module" + (i % 12).ToString(CultureInfo.InvariantCulture), "File" + i.ToString(CultureInfo.InvariantCulture) + ".cs"))).ToArray();

        var forward = await CheckoutAndMeasureAsync(context, root.RootPath, branch, markers, lease.Token);
        var back = await CheckoutAndMeasureAsync(context, root.RootPath, "master", [], lease.Token);
        var result = new JObject
        {
            ["branch"] = branch,
            ["filesChanged"] = 500,
            ["forward"] = forward,
            ["back"] = back,
            ["how"] = "git checkout run from the test (the IDE open, documents closed); clock from the start of git; searchable = five sampled files' new identifiers found by lexical SearchDetailedAsync; idle = GetIndexStatusAsync phase Idle after the markers",
        };
        await WriteGate2Async("branch-" + branch, result);
        Assert.True(forward["searchableSeconds"] is JValue { Type: JTokenType.Float or JTokenType.Integer }, "The branch's identifiers never became searchable: " + forward);
    }

    private static async Task<JObject> CheckoutAndMeasureAsync(IContextService context, string root, string branch, (string Marker, string File)[] markers, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var git = new ProcessStartInfo("git", $"-C \"{Gate2Folder}\" checkout -q {branch}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        using var process = System.Diagnostics.Process.Start(git)!;
        var stderr = await Task.Run(() => process.StandardError.ReadToEnd(), cancellationToken).ConfigureAwait(false);
        process.WaitForExit();
        var gitSeconds = clock.Elapsed.TotalSeconds;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git checkout {branch} failed: {stderr}");
        }

        double? searchable = null;
        var pending = markers.ToList();
        var sawBusy = false;
        IndexStatus? status = null;
        while (clock.Elapsed < TimeSpan.FromMinutes(10))
        {
            status = await context.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false);
            sawBusy |= status.IsIndexing || status.Phase != IndexPhase.Idle;
            foreach (var m in pending.ToList())
            {
                var response = await context.SearchDetailedAsync(new SearchRequest { Query = m.Marker, Mode = SearchMode.Lexical, MaxResults = 5, Roots = [root], IncludeText = true }, cancellationToken).ConfigureAwait(false);
                if (response.Results.Any(r => (r.Text ?? r.Snippet).Contains(m.Marker)))
                {
                    pending.Remove(m);
                }
            }

            if (pending.Count == 0 && searchable is null)
            {
                searchable = clock.Elapsed.TotalSeconds;
            }

            if (searchable is not null && status.Phase == IndexPhase.Idle && !status.IsIndexing && (sawBusy || clock.Elapsed > TimeSpan.FromSeconds(5)))
            {
                break;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return new JObject
        {
            ["branch"] = branch,
            ["gitSeconds"] = Math.Round(gitSeconds, 2),
            ["searchableSeconds"] = searchable is { } s ? Math.Round(s, 2) : null,
            ["idleSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 2),
            ["sawIndexing"] = sawBusy,
            ["finalPhase"] = status?.Phase.ToString(),
        };
    }

    /// <summary>Sends one chat request through the host with retrieval on (Local-strong) and collects the reply and its citations.</summary>
    private static async Task<(string Text, string Model, Citation[] Citations, string? Error)> ChatOnceAsync(IHostConnection connection, string requestId, string conversationId, string question, CancellationToken cancellationToken)
    {
        var request = new ChatRequest
        {
            RequestId = requestId,
            ConversationId = conversationId,
            Messages = [new ChatMessage { Role = ChatRole.User, Content = question }],
            Profile = Profile.LocalStrong,
            RetrievalMode = RetrievalMode.On,
            MaxRetrievedChunks = 8,
        };
        var text = new StringBuilder();
        var model = string.Empty;
        Citation[] citations = [];
        string? error = null;
        await foreach (var chunk in connection.GetProxy<IModelService>().ChatAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            text.Append(chunk.Text);
            if (!string.IsNullOrEmpty(chunk.ModelId))
            {
                model = chunk.ProviderId + "/" + chunk.ModelId;
            }

            if (chunk.IsFinal)
            {
                citations = chunk.Citations ?? [];
                error = chunk.Error?.Message;
                break;
            }
        }

        return (text.ToString(), model, citations, error);
    }
}
