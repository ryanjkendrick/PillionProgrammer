using System.Diagnostics;
using System.Text.Json;
using Pillion.Indexing.Ownership;

namespace Pillion.Indexing.Tests;

/// <summary>The writer.lock election within one process: two owners simulated with two probes.</summary>
public sealed class WriterLockTests
{
    private static readonly DateTimeOffset Started = FakeProcessTable.DefaultStart;

    private static string LockPath(TempDirectory temp) => temp.File("index/writer.lock");

    private static void WriteLockFile(string path, int processId, DateTimeOffset start, string pipe = "stale-pipe", string token = "stale-token")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version = 1,
            processId,
            processStartUtc = start,
            pipeName = pipe,
            acquiredUtc = start,
            token,
        }));
    }

    [Fact]
    public void TheFirstHostToTry_TakesTheLock_AndRecordsItself()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("index"));
        var table = new FakeProcessTable();
        var probe = table.HostProbe(100);
        var now = new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);

        var attempt = WriterLock.TryAcquire(LockPath(temp), "pillion-host-100", probe, new TestTime(now));

        Assert.True(attempt.Acquired);
        Assert.Null(attempt.Owner);
        var held = attempt.Lock!;
        Assert.Equal(100, held.Info.ProcessId);
        Assert.Equal(Started, held.Info.ProcessStartUtc);
        Assert.Equal("pillion-host-100", held.Info.PipeName);
        Assert.Equal(now, held.Info.AcquiredUtc);
        Assert.Equal(held.Info, WriterLock.Read(LockPath(temp)));
        Assert.Empty(Directory.GetFiles(temp.File("index"), "*.tmp"));
        Assert.Empty(Directory.GetFiles(temp.File("index"), "*.reclaim"));
    }

    [Fact]
    public void ASecondHost_FindsTheLiveOwner_AndLearnsItsPipe()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("index"));
        var table = new FakeProcessTable();
        using var first = WriterLock.TryAcquire(LockPath(temp), "pipe-100", table.HostProbe(100)).Lock!;

        var second = WriterLock.TryAcquire(LockPath(temp), "pipe-200", table.HostProbe(200));

        Assert.False(second.Acquired);
        Assert.Null(second.Lock);
        Assert.Equal(100, second.Owner!.ProcessId);
        Assert.Equal("pipe-100", second.Owner.PipeName);
        Assert.Equal(first.Info.Token, second.Owner.Token);
    }

    [Fact]
    public void TheSameProcessAsking_TwiceIsAReader_ThereIsOneOwnerPerProcessAndIndex()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("index"));
        var probe = new FakeProcessTable().HostProbe(100);
        using var first = WriterLock.TryAcquire(LockPath(temp), "pipe", probe).Lock!;

        var again = WriterLock.TryAcquire(LockPath(temp), "pipe", probe);

        Assert.False(again.Acquired);
        Assert.Equal(100, again.Owner!.ProcessId);
    }

    [Fact]
    public void ReleasingTheLock_LetsAnotherHostTakeIt()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("index"));
        var table = new FakeProcessTable();
        var first = WriterLock.TryAcquire(LockPath(temp), "pipe-100", table.HostProbe(100)).Lock!;

        first.Dispose();
        first.Dispose();

        Assert.False(File.Exists(LockPath(temp)));
        var second = WriterLock.TryAcquire(LockPath(temp), "pipe-200", table.HostProbe(200));
        Assert.True(second.Acquired);
        Assert.Equal(200, second.Lock!.Info.ProcessId);
    }

    [Fact]
    public void ALockOfADeadProcess_IsReclaimed()
    {
        using var temp = new TempDirectory();
        var table = new FakeProcessTable();
        WriteLockFile(LockPath(temp), processId: 4242, Started);

        var attempt = WriterLock.TryAcquire(LockPath(temp), "pipe-new", table.HostProbe(100));

        Assert.True(attempt.Acquired);
        Assert.Equal(100, WriterLock.Read(LockPath(temp))!.ProcessId);
        Assert.Equal("pipe-new", WriterLock.Read(LockPath(temp))!.PipeName);
        Assert.Empty(Directory.GetFiles(temp.File("index"), "*.reclaim"));
    }

    [Fact]
    public void ALockWhoseProcessIdWasReused_ByAProcessThatStartedLater_IsReclaimed()
    {
        using var temp = new TempDirectory();
        var table = new FakeProcessTable();
        table.SetAlive(4242, Started.AddHours(5));
        WriteLockFile(LockPath(temp), processId: 4242, Started);

        var attempt = WriterLock.TryAcquire(LockPath(temp), "pipe-new", table.HostProbe(100));

        Assert.True(attempt.Acquired);
    }

    [Fact]
    public void ALockWhoseProcessIsAliveWithTheRecordedStart_IsNotReclaimed()
    {
        using var temp = new TempDirectory();
        var table = new FakeProcessTable();
        table.SetAlive(4242, Started);
        WriteLockFile(LockPath(temp), processId: 4242, Started, pipe: "pipe-4242");

        var attempt = WriterLock.TryAcquire(LockPath(temp), "pipe-new", table.HostProbe(100));

        Assert.False(attempt.Acquired);
        Assert.Equal("pipe-4242", attempt.Owner!.PipeName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"processId\": 0, \"token\": \"x\"}")]
    [InlineData("[1,2,3]")]
    public void AnUnparseableLock_IsReclaimed(string content)
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("index"));
        File.WriteAllText(LockPath(temp), content);

        var attempt = WriterLock.TryAcquire(LockPath(temp), "pipe-new", new FakeProcessTable().HostProbe(100));

        Assert.True(attempt.Acquired);
        Assert.Equal("pipe-new", WriterLock.Read(LockPath(temp))!.PipeName);
    }

    [Fact]
    public void ALockTakenOverByAnotherHost_IsNotDeletedByTheOldOwnersRelease()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("index"));
        var table = new FakeProcessTable();
        var old = WriterLock.TryAcquire(LockPath(temp), "pipe-100", table.HostProbe(100)).Lock!;

        // The old owner is presumed dead and another host reclaims; the old owner then wakes up and disposes.
        table.Kill(100);
        var successor = WriterLock.TryAcquire(LockPath(temp), "pipe-200", table.HostProbe(200)).Lock!;
        old.Dispose();

        Assert.True(File.Exists(LockPath(temp)));
        Assert.Equal(successor.Info, WriterLock.Read(LockPath(temp)));
    }

    [Fact]
    public void Read_ReturnsNullWhenThereIsNoLock()
    {
        using var temp = new TempDirectory();

        Assert.Null(WriterLock.Read(LockPath(temp)));
    }

    [Fact]
    public void ManyThreadsTryingAtOnce_ProduceExactlyOneOwner()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("index"));
        var table = new FakeProcessTable();
        const int Hosts = 24;
        var probes = Enumerable.Range(0, Hosts).Select(i => table.HostProbe(1000 + i)).ToList();

        var attempts = RunTogether(Hosts, i => WriterLock.TryAcquire(LockPath(temp), $"pipe-{i}", probes[i]));

        var winners = attempts.Where(a => a.Acquired).ToList();
        var winner = Assert.Single(winners);
        Assert.All(attempts.Where(a => !a.Acquired), a => Assert.Equal(winner.Lock!.Info.Token, a.Owner!.Token));
        Assert.Equal(winner.Lock!.Info, WriterLock.Read(LockPath(temp)));
    }

    [Fact]
    public void ManyThreadsReclaimingTheSameStaleLock_ProduceExactlyOneOwner_AndNobodyDeletesTheWinnersLock()
    {
        for (var round = 0; round < 10; round++)
        {
            using var temp = new TempDirectory();
            var table = new FakeProcessTable();
            const int Hosts = 16;
            var probes = Enumerable.Range(0, Hosts).Select(i => table.HostProbe(2000 + i)).ToList();
            WriteLockFile(LockPath(temp), processId: 999, Started);

            var attempts = RunTogether(Hosts, i => WriterLock.TryAcquire(LockPath(temp), $"pipe-{i}", probes[i]));

            var winner = Assert.Single(attempts, a => a.Acquired);
            Assert.All(attempts.Where(a => !a.Acquired), a => Assert.Equal(winner.Lock!.Info.Token, a.Owner!.Token));
            Assert.Equal(winner.Lock!.Info, WriterLock.Read(LockPath(temp)));
            Assert.Empty(Directory.GetFiles(temp.File("index"), "*.reclaim"));
        }
    }

    [Fact]
    public async Task AStaleReclaimGuardLeftByAMachineCrash_DoesNotBlockReclaiming()
    {
        using var temp = new TempDirectory();
        var table = new FakeProcessTable();
        WriteLockFile(LockPath(temp), processId: 999, Started);
        var guard = LockPath(temp) + ".reclaim";
        File.WriteAllText(guard, string.Empty);
        File.SetLastWriteTimeUtc(guard, DateTime.UtcNow.AddHours(-2));

        var attempt = await Task.Run(() => WriterLock.TryAcquire(LockPath(temp), "pipe", table.HostProbe(100)));

        Assert.True(attempt.Acquired);
    }

    private static T[] RunTogether<T>(int count, Func<int, T> work)
    {
        using var gate = new ManualResetEventSlim(false);
        var results = new T[count];
        var threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
        {
            gate.Wait();
            results[i] = work(i);
        })).ToList();
        threads.ForEach(t => t.Start());
        Thread.Sleep(50);
        gate.Set();
        threads.ForEach(t => Assert.True(t.Join(TimeSpan.FromSeconds(60))));
        return results;
    }
}

/// <summary>The same election across real processes: the test assembly started as a helper.</summary>
public sealed class CrossProcessLockTests
{
    private static string HelperAssembly => typeof(LockHelper).Assembly.Location;

    private sealed class Helper : IDisposable
    {
        private readonly Process _process;

        private Helper(Process process)
        {
            _process = process;
        }

        public int Id => _process.Id;

        public static Helper Start(string lockPath, string pipeName, string startFile)
        {
            var info = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add(HelperAssembly);
            info.ArgumentList.Add(LockHelper.Command);
            info.ArgumentList.Add("acquire");
            info.ArgumentList.Add(lockPath);
            info.ArgumentList.Add(pipeName);
            info.ArgumentList.Add(startFile);
            return new Helper(Process.Start(info)!);
        }

        public async Task<string> ReadLineAsync()
        {
            var read = _process.StandardOutput.ReadLineAsync();
            var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(finished == read, "the helper did not answer in 60 seconds: " + SafeStandardError());
            return (await read) ?? throw new InvalidOperationException("The helper exited without answering: " + SafeStandardError());
        }

        public Task SendAsync(string line) => _process.StandardInput.WriteLineAsync(line);

        public void Kill() => _process.Kill(entireProcessTree: true);

        public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            _process.Dispose();
        }

        private string SafeStandardError()
        {
            try
            {
                return _process.HasExited ? _process.StandardError.ReadToEnd() : "(still running)";
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }
    }

    [Fact]
    public async Task SeveralProcessesTryingAtOnce_ProduceExactlyOneOwner_AndTheOthersLearnItsPipe()
    {
        for (var round = 0; round < 3; round++)
        {
            using var temp = new TempDirectory();
            Directory.CreateDirectory(temp.File("index"));
            var lockPath = temp.File("index/writer.lock");
            var startFile = temp.File("go");
            var helpers = Enumerable.Range(0, 4).Select(i => Helper.Start(lockPath, $"pipe-helper-{i}", startFile)).ToList();
            try
            {
                // Give every helper time to start up and park on the start file, then release them together.
                await Task.Delay(1500);
                File.WriteAllText(startFile, "go");

                var answers = new List<string>();
                foreach (var helper in helpers)
                {
                    answers.Add(await helper.ReadLineAsync());
                }

                var owners = answers.Where(a => a.StartsWith("OWNER ", StringComparison.Ordinal)).ToList();
                var ownerLine = Assert.Single(owners);
                var ownerIndex = answers.IndexOf(ownerLine);
                var ownerPid = int.Parse(ownerLine["OWNER ".Length..], System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(helpers[ownerIndex].Id, ownerPid);
                foreach (var follower in answers.Where(a => a != ownerLine))
                {
                    Assert.Equal($"FOLLOWER {ownerPid} pipe-helper-{ownerIndex}", follower);
                }

                Assert.Equal(ownerPid, WriterLock.Read(lockPath)!.ProcessId);

                // The owner lets go cleanly; the file disappears.
                await helpers[ownerIndex].SendAsync("release");
                Assert.Equal("RELEASED", await helpers[ownerIndex].ReadLineAsync());
                Assert.True(helpers[ownerIndex].WaitForExit(TimeSpan.FromSeconds(30)));
                Assert.False(File.Exists(lockPath));
            }
            finally
            {
                helpers.ForEach(h => h.Dispose());
            }
        }
    }

    [Fact]
    public async Task AnOwnerThatDiesWithoutReleasing_HasItsLockReclaimedByAnotherProcess()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("index"));
        var lockPath = temp.File("index/writer.lock");
        var startFile = temp.File("go");
        File.WriteAllText(startFile, "go");
        using var owner = Helper.Start(lockPath, "pipe-of-the-owner", startFile);
        Assert.StartsWith("OWNER ", await owner.ReadLineAsync(), StringComparison.Ordinal);

        // While the owner lives, this process is a follower and learns its pipe and process id.
        var follower = WriterLock.TryAcquire(lockPath, "pipe-of-this-process");
        Assert.False(follower.Acquired);
        Assert.Equal(owner.Id, follower.Owner!.ProcessId);
        Assert.Equal("pipe-of-the-owner", follower.Owner.PipeName);

        // Killed without a chance to clean up, its lock file stays behind, and this process takes over.
        owner.Kill();
        Assert.True(owner.WaitForExit(TimeSpan.FromSeconds(30)));
        Assert.True(File.Exists(lockPath));
        var takeover = WriterLock.TryAcquire(lockPath, "pipe-of-this-process");

        Assert.True(takeover.Acquired);
        Assert.Equal(Environment.ProcessId, takeover.Lock!.Info.ProcessId);
        Assert.Equal("pipe-of-this-process", WriterLock.Read(lockPath)!.PipeName);
        takeover.Lock.Dispose();
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task ARealProcessIdWithTheWrongStartTime_IsStale()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("index"));
        var lockPath = temp.File("index/writer.lock");

        // This very process, but recorded as having started a day earlier: what a reused process id looks like.
        var self = Process.GetCurrentProcess();
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new
        {
            version = 1,
            processId = self.Id,
            processStartUtc = new DateTimeOffset(self.StartTime.ToUniversalTime()).AddDays(-1),
            pipeName = "pipe-of-a-previous-life",
            acquiredUtc = DateTimeOffset.UtcNow.AddDays(-1),
            token = "old",
        }));

        var attempt = await Task.Run(() => WriterLock.TryAcquire(lockPath, "pipe-now"));

        Assert.True(attempt.Acquired);
        Assert.Equal("pipe-now", WriterLock.Read(lockPath)!.PipeName);
    }

    [Fact]
    public void TheRealProbe_KnowsLiveAndDeadProcesses()
    {
        var self = Process.GetCurrentProcess();
        var start = new DateTimeOffset(self.StartTime.ToUniversalTime());

        Assert.True(ProcessProbe.Instance.IsAlive(Environment.ProcessId, start));
        Assert.Equal(Environment.ProcessId, ProcessProbe.Instance.CurrentProcessId);
        Assert.True((ProcessProbe.Instance.CurrentProcessStart - start).Duration() < TimeSpan.FromSeconds(1));
        Assert.False(ProcessProbe.Instance.IsAlive(Environment.ProcessId, start.AddDays(-1)));
        Assert.False(ProcessProbe.Instance.IsAlive(int.MaxValue - 7, start));
    }
}
