using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>What the package-load measurement of <c>PillionPackageTests</c> saw (UI thread CPU times and wall-clock spans).</summary>
/// <param name="LoadStartSource">Where the start of the package load came from.</param>
/// <param name="FolderOpenToReady">Wall-clock time from the folder open to the host's Ready.</param>
/// <param name="LoadToReady">Wall-clock time from the package load start to Ready.</param>
/// <param name="UiCpuFolderOpenToReady">UI thread CPU time from the folder open to Ready.</param>
/// <param name="UiCpuLoadToReady">UI thread CPU time from the package load start to Ready.</param>
/// <param name="UiCpuIdleBaseline">UI thread CPU time over an idle stretch after Ready.</param>
/// <param name="IdleLength">The length of that idle stretch.</param>
/// <param name="Samples">The number of samples taken.</param>
public sealed record PackageLoadCpuReport(
    string LoadStartSource,
    TimeSpan FolderOpenToReady,
    TimeSpan LoadToReady,
    TimeSpan UiCpuFolderOpenToReady,
    TimeSpan UiCpuLoadToReady,
    TimeSpan UiCpuIdleBaseline,
    TimeSpan IdleLength,
    int Samples)
{
    /// <summary>Gets or sets what Pillion's own UI-thread timings (the product's debug switch) recorded from the folder open to Ready.</summary>
    public PackageLoadTimings? PillionTimed { get; init; }

    /// <inheritdoc/>
    public override string ToString() => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        "UI thread CPU from the package load start ({0}) to Ready: {1:0} ms over {2:0} ms; from the folder open to Ready: {3:0} ms over {4:0} ms; idle baseline after Ready: {5:0} ms over {6:0} ms ({7} samples). Pillion's own timed UI-thread work from the folder open to Ready: {8}.",
        LoadStartSource,
        UiCpuLoadToReady.TotalMilliseconds,
        LoadToReady.TotalMilliseconds,
        UiCpuFolderOpenToReady.TotalMilliseconds,
        FolderOpenToReady.TotalMilliseconds,
        UiCpuIdleBaseline.TotalMilliseconds,
        IdleLength.TotalMilliseconds,
        Samples,
        PillionTimed?.ToString() ?? "not recorded");
}

/// <summary>
/// Samples the CPU time of one thread (the UI thread) from a dedicated background thread, with the wall-clock time of
/// each sample, so the CPU the thread used between two moments (for example the Activity Log's "Begin package load" and
/// the host's Ready) can be read afterwards. Reading another thread's times needs no help from that thread, so a busy or
/// blocked UI thread does not delay the samples.
/// </summary>
public sealed class UiThreadCpuSampler : IDisposable
{
    private readonly ThreadCpuMeter _meter;
    private readonly TimeSpan _interval;
    private readonly List<(DateTime Utc, TimeSpan Cpu)> _samples = [];
    private readonly object _gate = new();
    private readonly Thread _thread;
    private volatile bool _stop;

    private UiThreadCpuSampler(uint threadId, TimeSpan interval)
    {
        _meter = ThreadCpuMeter.ForThread(threadId);
        _interval = interval;
        Sample();
        _thread = new Thread(Run) { IsBackground = true, Name = "Pillion UI thread CPU sampler", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Starts sampling a thread.</summary>
    /// <param name="threadId">The native id of the thread (<see cref="ThreadCpuMeter.CurrentThreadId"/> read on it).</param>
    /// <param name="interval">The time between samples.</param>
    /// <returns>The running sampler; dispose it.</returns>
    public static UiThreadCpuSampler Start(uint threadId, TimeSpan interval) => new(threadId, interval);

    /// <summary>Gets the number of samples taken so far.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>Takes one more sample now and returns its time (a mark to measure from or to).</summary>
    /// <returns>The time of the sample (UTC).</returns>
    public DateTime Mark() => Sample();

    /// <summary>Gets the CPU time the thread used between two moments, from the samples nearest to them.</summary>
    /// <param name="fromUtc">The start (UTC).</param>
    /// <param name="toUtc">The end (UTC).</param>
    /// <returns>The CPU time.</returns>
    public TimeSpan CpuBetween(DateTime fromUtc, DateTime toUtc)
    {
        lock (_gate)
        {
            return At(toUtc) - At(fromUtc);
        }
    }

    /// <summary>Stops sampling.</summary>
    public void Stop()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        _meter.Dispose();
    }

    private TimeSpan At(DateTime utc)
    {
        // The last sample at or before the moment (the thread's CPU time can only grow).
        var before = _samples.LastOrDefault(s => s.Utc <= utc);
        return before.Utc == default ? _samples[0].Cpu : before.Cpu;
    }

    private DateTime Sample()
    {
        var now = DateTime.UtcNow;
        var cpu = _meter.Read();
        lock (_gate)
        {
            _samples.Add((now, cpu));
        }

        return now;
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        while (!_stop)
        {
            Thread.Sleep(_interval);
            try
            {
                Sample();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return; // The thread is gone (Visual Studio is closing).
            }

            if (clock.Elapsed > TimeSpan.FromMinutes(10))
            {
                return; // A forgotten sampler stops by itself.
            }
        }
    }
}
