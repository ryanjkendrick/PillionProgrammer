using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The Tools &gt; Options &gt; Pillion Programmer &gt; Indexing page (work item 2.15): the exclude patterns
/// (<c>indexing.exclude</c>), the memory ceiling (<c>indexing.maxMemoryMb</c>), pausing on battery saver
/// (<c>indexing.pauseOnBattery</c>) and the retention in days (<c>indexing.retentionDays</c>), with a read-only line naming the
/// active embedding model.
/// </summary>
/// <remarks>
/// Saves only the keys the developer changed, one <see cref="ISettingsService.SetAsync"/> each, under their own dotted key
/// (<c>indexing.maxMemoryMb</c>, never the whole <c>indexing</c> object), so a save never overwrites a value another page or
/// an editor changed meanwhile and never touches another section (tech-debt issue #57 is the same mistake on the Privacy page).
/// </remarks>
public sealed class IndexingOptionsViewModel : OptionsPageViewModel
{
    /// <summary>The settings section the page shows.</summary>
    public const string SectionKey = "indexing";

    /// <summary>The key of <c>indexing.exclude</c>.</summary>
    public const string ExcludeKey = "indexing.exclude";

    /// <summary>The key of <c>indexing.maxMemoryMb</c>.</summary>
    public const string MaxMemoryKey = "indexing.maxMemoryMb";

    /// <summary>The key of <c>indexing.pauseOnBattery</c>.</summary>
    public const string PauseOnBatteryKey = "indexing.pauseOnBattery";

    /// <summary>The key of <c>indexing.retentionDays</c>.</summary>
    public const string RetentionKey = "indexing.retentionDays";

    /// <summary>The value of <c>maxMemoryMb</c> when the setting is missing (the host's default).</summary>
    public const int DefaultMaxMemoryMb = 1536;

    /// <summary>The value of <c>retentionDays</c> when the setting is missing (design section 10: 30 days).</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>The least memory ceiling the page accepts, in megabytes.</summary>
    public const int MinMaxMemoryMb = 256;

    /// <summary>The greatest memory ceiling the page accepts, in megabytes.</summary>
    public const int MaxMaxMemoryMb = 65536;

    /// <summary>The greatest retention the page accepts, in days (ten years).</summary>
    public const int MaxRetentionDays = 3650;

    private static readonly TimeSpan ModelTimeout = TimeSpan.FromSeconds(5);

    private string _excludeText = string.Empty;
    private string _maxMemoryText = DefaultMaxMemoryMb.ToString(CultureInfo.InvariantCulture);
    private string _retentionText = DefaultRetentionDays.ToString(CultureInfo.InvariantCulture);
    private bool _pauseOnBattery = true;
    private string _modelText = IndexStrings.OptionsModelNone;
    private IReadOnlyList<string> _loadedExclude = [];
    private int _loadedMaxMemory = DefaultMaxMemoryMb;
    private int _loadedRetention = DefaultRetentionDays;
    private bool _loadedPauseOnBattery = true;

    /// <summary>Initializes a new instance of the <see cref="IndexingOptionsViewModel"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    public IndexingOptionsViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, PillionLog? log = null)
        : base(connection, joinableTaskFactory, log)
    {
    }

    /// <summary>Gets or sets the exclude patterns, one per line.</summary>
    public string ExcludeText
    {
        get => _excludeText;
        set
        {
            if (SetProperty(ref _excludeText, value ?? string.Empty))
            {
                IsDirty = true;
            }
        }
    }

    /// <summary>Gets or sets the memory ceiling in megabytes, as typed.</summary>
    public string MaxMemoryText
    {
        get => _maxMemoryText;
        set
        {
            if (SetProperty(ref _maxMemoryText, value ?? string.Empty))
            {
                IsDirty = true;
            }
        }
    }

    /// <summary>Gets or sets a value indicating whether indexing pauses on battery saver.</summary>
    public bool PauseOnBattery
    {
        get => _pauseOnBattery;
        set
        {
            if (SetProperty(ref _pauseOnBattery, value))
            {
                IsDirty = true;
            }
        }
    }

    /// <summary>Gets or sets the retention in days, as typed.</summary>
    public string RetentionText
    {
        get => _retentionText;
        set
        {
            if (SetProperty(ref _retentionText, value ?? string.Empty))
            {
                IsDirty = true;
            }
        }
    }

    /// <summary>Gets the read-only line that names the active embedding model.</summary>
    public string ModelText
    {
        get => _modelText;
        private set => SetProperty(ref _modelText, value);
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<string> WatchedSections { get; } = [SectionKey];

    /// <summary>Splits the exclude box into patterns: one per line, trimmed, without blank lines or repeats.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The patterns, in order.</returns>
    public static IReadOnlyList<string> SplitPatterns(string? text) =>
        (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <inheritdoc/>
    protected override async Task<Action<OptionsPageViewModel>> LoadCoreAsync(ISettingsService settings, CancellationToken cancellationToken)
    {
        var section = SettingsJsonText.ReadObject(await settings.GetAsync(SectionKey, cancellationToken).ConfigureAwait(false));
        var model = await TryReadModelAsync(cancellationToken).ConfigureAwait(false);
        return page => ((IndexingOptionsViewModel)page).ApplyLoaded(section, model);
    }

    /// <inheritdoc/>
    protected override string? Validate()
    {
        if (!TryParseMemory(MaxMemoryText, out _))
        {
            return IndexStrings.Format(IndexStrings.OptionsMaxMemoryInvalidFormat, MinMaxMemoryMb, MaxMaxMemoryMb);
        }

        return TryParseRetention(RetentionText, out _)
            ? null
            : IndexStrings.Format(IndexStrings.OptionsRetentionInvalidFormat, 0, MaxRetentionDays);
    }

    /// <inheritdoc/>
    protected override Func<ISettingsService, CancellationToken, Task<Action<OptionsPageViewModel>?>> PrepareSave()
    {
        var writes = new List<(string Key, string Json)>();
        var exclude = SplitPatterns(ExcludeText);

        // Validate() has accepted both; a value that still does not parse keeps what was loaded.
        var maxMemory = TryParseMemory(MaxMemoryText, out var typedMemory) ? typedMemory : _loadedMaxMemory;
        var retention = TryParseRetention(RetentionText, out var typedRetention) ? typedRetention : _loadedRetention;
        var pauseOnBattery = PauseOnBattery;

        if (!exclude.SequenceEqual(_loadedExclude, StringComparer.Ordinal))
        {
            writes.Add((ExcludeKey, SettingsJsonText.Write(new JArray(exclude.Cast<object>().ToArray()))));
        }

        if (maxMemory != _loadedMaxMemory)
        {
            writes.Add((MaxMemoryKey, maxMemory.ToString(CultureInfo.InvariantCulture)));
        }

        if (pauseOnBattery != _loadedPauseOnBattery)
        {
            writes.Add((PauseOnBatteryKey, SettingsJsonText.Write(pauseOnBattery)));
        }

        if (retention != _loadedRetention)
        {
            writes.Add((RetentionKey, retention.ToString(CultureInfo.InvariantCulture)));
        }

        return async (settings, cancellationToken) =>
        {
            foreach (var (key, json) in writes)
            {
                await settings.SetAsync(key, json, cancellationToken).ConfigureAwait(false);
            }

            return page => ((IndexingOptionsViewModel)page).ApplySaved(exclude, maxMemory, pauseOnBattery, retention);
        };
    }

    private static bool TryParseMemory(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value is >= MinMaxMemoryMb and <= MaxMaxMemoryMb;

    private static bool TryParseRetention(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value is >= 0 and <= MaxRetentionDays;

    private static int ReadInt(JObject section, string name, int fallback) =>
        section[name] is JValue { Type: JTokenType.Integer } value ? (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)value)) : fallback;

    /// <summary>The active model is the one the open index was built with, which the host reports in its status; best effort.</summary>
    private async Task<string?> TryReadModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelTimeout);
            var status = await Connection.GetProxy<IContextService>().GetIndexStatusAsync(timeout.Token).ConfigureAwait(false);
            return status?.Roots?.Select(r => r.EmbeddingModelId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The model line is only information; the page works without it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log?.Debug("options.indexing.model.unavailable", "Could not read the active embedding model.", ("error", ex.GetType().Name));
            return null;
        }
    }

    private void ApplyLoaded(JObject section, string? model)
    {
        var exclude = section["exclude"] is JArray array
            ? array.OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (string)v!).ToArray()
            : [];
        _loadedExclude = SplitPatterns(string.Join("\n", exclude));
        _loadedMaxMemory = ReadInt(section, "maxMemoryMb", DefaultMaxMemoryMb);
        _loadedRetention = ReadInt(section, "retentionDays", DefaultRetentionDays);
        _loadedPauseOnBattery = section["pauseOnBattery"] is JValue { Type: JTokenType.Boolean } battery ? (bool)battery : true;

        _excludeText = string.Join(Environment.NewLine, _loadedExclude);
        _maxMemoryText = _loadedMaxMemory.ToString(CultureInfo.CurrentCulture);
        _retentionText = _loadedRetention.ToString(CultureInfo.CurrentCulture);
        _pauseOnBattery = _loadedPauseOnBattery;
        _modelText = string.IsNullOrWhiteSpace(model) ? IndexStrings.OptionsModelNone : IndexStrings.Format(IndexStrings.OptionsModelFormat, model);
        OnPropertyChanged(nameof(ExcludeText));
        OnPropertyChanged(nameof(MaxMemoryText));
        OnPropertyChanged(nameof(RetentionText));
        OnPropertyChanged(nameof(PauseOnBattery));
        OnPropertyChanged(nameof(ModelText));
    }

    private void ApplySaved(IReadOnlyList<string> exclude, int maxMemory, bool pauseOnBattery, int retention)
    {
        _loadedExclude = exclude;
        _loadedMaxMemory = maxMemory;
        _loadedPauseOnBattery = pauseOnBattery;
        _loadedRetention = retention;
    }
}
