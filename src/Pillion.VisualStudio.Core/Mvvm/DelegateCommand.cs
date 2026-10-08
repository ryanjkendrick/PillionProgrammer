using System;
using System.Windows.Input;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// An <see cref="ICommand"/> that calls delegates. <see cref="CanExecuteChanged"/> is raised only by
/// <see cref="RaiseCanExecuteChanged"/>, which view models call on the UI thread when the inputs change.
/// </summary>
public sealed class DelegateCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>Initializes a new instance of the <see cref="DelegateCommand"/> class.</summary>
    /// <param name="execute">Runs the command.</param>
    /// <param name="canExecute">Says whether the command can run, or <see langword="null"/> for always.</param>
    public DelegateCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>Initializes a new instance of the <see cref="DelegateCommand"/> class for a command without a parameter.</summary>
    /// <param name="execute">Runs the command.</param>
    /// <param name="canExecute">Says whether the command can run, or <see langword="null"/> for always.</param>
    public DelegateCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
        if (execute is null)
        {
            throw new ArgumentNullException(nameof(execute));
        }
    }

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc/>
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute(parameter);
        }
    }

    /// <summary>Raises <see cref="CanExecuteChanged"/>. Call on the UI thread.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
