using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Threading;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.Signals;

/// <summary>
/// Reads the Error List through its table control (<see cref="IErrorList.TableControl"/>): the same entries the developer
/// sees, so the Error List's own filters (scope, Build + IntelliSense, severity buttons) apply. Handles are collected on
/// the UI thread, then read in batches of <see cref="BatchSize"/> with the UI thread released in between.
/// </summary>
internal sealed class ErrorListSource : IErrorListSource
{
    /// <summary>How many entries are read per visit to the UI thread.</summary>
    private const int BatchSize = 200;

    /// <summary>The most entries returned; the Error List of a large failing solution can hold tens of thousands.</summary>
    private const int MaxEntries = 2000;

    private readonly IErrorList _errorList;
    private readonly JoinableTaskFactory _joinableTaskFactory;

    /// <summary>Initializes a new instance of the <see cref="ErrorListSource"/> class.</summary>
    /// <param name="errorList">The Error List service.</param>
    /// <param name="joinableTaskFactory">Switches to the UI thread.</param>
    public ErrorListSource(IErrorList errorList, JoinableTaskFactory joinableTaskFactory)
    {
        _errorList = errorList;
        _joinableTaskFactory = joinableTaskFactory;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DiagnosticInfo>> ReadAsync(CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var handles = _errorList.TableControl.Entries.Take(MaxEntries).ToList();

        var result = new List<DiagnosticInfo>(handles.Count);
        for (var offset = 0; offset < handles.Count; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            foreach (var handle in handles.Skip(offset).Take(BatchSize))
            {
                if (ErrorListMapper.Map(new HandleEntry(handle)) is { } diagnostic)
                {
                    result.Add(diagnostic);
                }
            }

            // Let the UI thread run between batches.
            await TaskScheduler.Default;
        }

        return result;
    }

    private sealed class HandleEntry(ITableEntryHandle handle) : IErrorListRow
    {
        public bool TryGetValue(string keyName, out object? value)
        {
            var found = handle.TryGetValue(keyName, out var content);
            value = content;
            return found;
        }
    }
}
