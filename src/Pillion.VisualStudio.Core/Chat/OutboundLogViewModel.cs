using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>One row of the Outbound log view: a request sent to a model, as text for the columns. Never prompt content.</summary>
public sealed class OutboundRecordRow
{
    /// <summary>Initializes a new instance of the <see cref="OutboundRecordRow"/> class.</summary>
    /// <param name="record">The host's record.</param>
    public OutboundRecordRow(OutboundRecordDto record)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        var utc = DateTime.SpecifyKind(record.TimestampUtc, DateTimeKind.Utc);
        Time = utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        TaskClass = record.TaskClass.ToString();
        ProviderModel = record.ProviderId + " / " + record.ModelId;
        Locality = record.IsLocal ? ChatStrings.LocalityLocal : ChatStrings.LocalityCloud;
        Tokens = record.CacheHit
            ? ChatStrings.OutboundCacheHit
            : ChatStrings.Format(ChatStrings.OutboundTokensFormat, record.PromptTokens.ToString("N0", CultureInfo.CurrentCulture), record.CompletionTokens.ToString("N0", CultureInfo.CurrentCulture));
        Redactions = record.RedactionCount.ToString(CultureInfo.CurrentCulture);
        Duration = record.DurationMs < 1000
            ? record.DurationMs.ToString(CultureInfo.CurrentCulture) + " ms"
            : (record.DurationMs / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " s";
        Outcome = record.ErrorCode is { } code ? record.Outcome + " (" + code + ")" : record.Outcome;
        Context = record.ContextItems == 0
            ? string.Empty
            : ChatStrings.Format(ChatStrings.OutboundContextFormat, record.ContextItems, record.ContextTrimmed);
    }

    /// <summary>Gets the host's record.</summary>
    public OutboundRecordDto Record { get; }

    /// <summary>Gets the local time the request was sent.</summary>
    public string Time { get; }

    /// <summary>Gets the task class.</summary>
    public string TaskClass { get; }

    /// <summary>Gets the provider and model.</summary>
    public string ProviderModel { get; }

    /// <summary>Gets <c>local</c> or <c>cloud</c>.</summary>
    public string Locality { get; }

    /// <summary>Gets the prompt and completion tokens.</summary>
    public string Tokens { get; }

    /// <summary>Gets the number of secrets replaced.</summary>
    public string Redactions { get; }

    /// <summary>Gets how long the request took.</summary>
    public string Duration { get; }

    /// <summary>Gets how it ended (with the error code when it failed).</summary>
    public string Outcome { get; }

    /// <summary>Gets the context items and how many were cut or left out, for an assembled chat request; else empty.</summary>
    public string Context { get; }

    /// <summary>Gets the name a screen reader announces for the row.</summary>
    public string AutomationName => ToText(", ");

    /// <summary>The row as text, with a separator between the columns.</summary>
    /// <param name="separator">The separator.</param>
    /// <returns>The text.</returns>
    public string ToText(string separator) =>
        string.Join(separator, Time, TaskClass, ProviderModel, Locality, Tokens, Redactions, Duration, Outcome);
}

/// <summary>
/// The Outbound log view of the chat window (work item 1.13; design section 12): the last 200 requests Pillion Host sent to
/// any model, from every task class, newest first, read with <see cref="IDiagnosticsService.GetOutboundRecordsAsync"/>.
/// Refreshed after each reply and on demand; copied as tab-separated text. It holds counts and durations only. Call public
/// members on the UI thread.
/// </summary>
public sealed class OutboundLogViewModel : ObservableObject
{
    /// <summary>The most records shown.</summary>
    public const int MaxRecords = 200;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly IHostConnection _connection;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly Action<string>? _copyText;
    private readonly PillionLog? _log;
    private readonly CancellationToken _disposal;
    private int _version;
    private bool _isLoading;
    private bool _isExpanded;
    private string _error = string.Empty;
    private string _summary = ChatStrings.OutboundEmpty;

    /// <summary>Initializes a new instance of the <see cref="OutboundLogViewModel"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="copyText">Puts text on the clipboard; Copy is disabled without it.</param>
    /// <param name="log">The logger.</param>
    /// <param name="disposal">Cancelled when the window closes.</param>
    public OutboundLogViewModel(IHostConnection connection, JoinableTaskFactory joinableTaskFactory, Action<string>? copyText, PillionLog? log, CancellationToken disposal)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _copyText = copyText;
        _log = log;
        _disposal = disposal;
        RefreshCommand = new DelegateCommand(() => _ = _joinableTaskFactory.RunAsync(RefreshAsync), () => !IsLoading);
        CopyCommand = new DelegateCommand(Copy, () => _copyText is not null && Records.Count > 0);
    }

    /// <summary>Gets the rows, newest first.</summary>
    public ObservableCollection<OutboundRecordRow> Records { get; } = [];

    /// <summary>Gets or sets a value indicating whether the view is expanded.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Gets a value indicating whether the records are being read.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets why the records could not be read, or empty.</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="Error"/> has text.</summary>
    public bool HasError => _error.Length > 0;

    /// <summary>Gets the line beside the header ("12 requests").</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Gets the Refresh command.</summary>
    public DelegateCommand RefreshCommand { get; }

    /// <summary>Gets the Copy command (the rows as tab-separated text with a header line).</summary>
    public DelegateCommand CopyCommand { get; }

    /// <summary>Reads the records from the host. Never throws (except when the window closes); a failure is shown.</summary>
    /// <returns>A task that completes when the rows have been updated.</returns>
    public async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _version);
        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal);
        IsLoading = true;
        await TaskScheduler.Default;

        OutboundRecordDto[]? records = null;
        RpcFailure? failure = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposal);
            timeout.CancelAfter(ReadTimeout);
            records = await _connection.GetProxy<IDiagnosticsService>().GetOutboundRecordsAsync(MaxRecords, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposal.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Any failure is shown in the view.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = RpcFailure.Describe(ex);
            _log?.Debug("chat.outbound.failed", "Reading the Outbound log failed.", ("error", ex.GetType().Name), ("code", failure.Code?.ToString()));
        }

        await _joinableTaskFactory.SwitchToMainThreadAsync(_disposal);
        if (version != Volatile.Read(ref _version))
        {
            return; // A newer read is on its way.
        }

        IsLoading = false;
        if (records is null)
        {
            Error = failure?.Message ?? ChatStrings.OutboundFailed;
            return;
        }

        Error = string.Empty;
        Records.Clear();
        foreach (var record in records.Where(r => r is not null).OrderByDescending(r => r.TimestampUtc).Take(MaxRecords))
        {
            Records.Add(new OutboundRecordRow(record));
        }

        Summary = Records.Count == 0 ? ChatStrings.OutboundEmpty : ChatStrings.Format(ChatStrings.OutboundCountFormat, Records.Count);
        CopyCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The rows as tab-separated text with a header line.</summary>
    /// <returns>The text.</returns>
    public string ToText() => ToText(Records);

    /// <summary>Formats rows as tab-separated text with a header line.</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The text.</returns>
    public static string ToText(IEnumerable<OutboundRecordRow> rows)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join("\t", ChatStrings.OutboundColumnTime, ChatStrings.OutboundColumnTaskClass, ChatStrings.OutboundColumnModel, ChatStrings.OutboundColumnLocality, ChatStrings.OutboundColumnTokens, ChatStrings.OutboundColumnRedactions, ChatStrings.OutboundColumnDuration, ChatStrings.OutboundColumnOutcome));
        foreach (var row in rows ?? [])
        {
            builder.Append(Environment.NewLine).Append(row.ToText("\t"));
        }

        return builder.ToString();
    }

    private void Copy() => _copyText?.Invoke(ToText());
}
