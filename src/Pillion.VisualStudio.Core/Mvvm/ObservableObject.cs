using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// Base class for view models: raises <see cref="PropertyChanged"/>. View models are only changed on the UI thread
/// (Visual Studio's main thread), so the events reach WPF bindings on the thread that owns them.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    /// <param name="propertyName">The name of the property that changed.</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Sets a backing field and raises <see cref="PropertyChanged"/> when the value changes.</summary>
    /// <typeparam name="T">The property type.</typeparam>
    /// <param name="field">The backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="propertyName">The name of the property.</param>
    /// <returns><see langword="true"/> when the value changed.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
