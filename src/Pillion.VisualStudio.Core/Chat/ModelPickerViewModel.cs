using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The chat window's model picker: Auto first, then the models the host listed, local first, with models greyed out
/// and a reason when the settings rule them out or the host lists them as not available. The view groups
/// <see cref="Models"/> by <see cref="ModelOption.Group"/>.
/// </summary>
public sealed class ModelPickerViewModel : ObservableObject
{
    private ModelOption? _selectedModel;
    private string _message = "Waiting for Pillion Host.";
    private bool _isLoading;

    /// <summary>Raised when <see cref="SelectedModel"/> changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Gets the models, local first, then cloud.</summary>
    public ObservableCollection<ModelOption> Models { get; } = [];

    /// <summary>Gets or sets the picked model. An unavailable model cannot be picked.</summary>
    public ModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (value is { IsAvailable: false })
            {
                // Keep the current choice; raise so the view snaps back to it.
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _selectedModel, value))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Gets a note shown beside the picker (loading, no models, or why the list failed), or an empty string.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="Message"/> is not empty.</summary>
    public bool HasMessage => Message.Length > 0;

    /// <summary>Gets a value indicating whether the list is being fetched.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>Marks the list as loading. Call on the UI thread.</summary>
    internal void BeginLoading()
    {
        IsLoading = true;
        if (Models.Count == 0)
        {
            Message = "Loading models...";
        }
    }

    /// <summary>
    /// Replaces the list, with the Auto entry first whenever the host lists any model. Keeps the picked entry when it
    /// is still listed and available; otherwise picks the first available local model, then the first available cloud
    /// model, then Auto. Call on the UI thread.
    /// </summary>
    /// <param name="listed">The new entries, already ordered by <see cref="ModelOption.Build"/>.</param>
    internal void SetModels(IReadOnlyList<ModelOption> listed)
    {
        IReadOnlyList<ModelOption> models = listed.Count > 0 && !listed.Any(m => m.IsAuto)
            ? [ModelOption.CreateAuto(listed), .. listed]
            : listed;
        var previousId = SelectedModel?.Id;
        Models.Clear();
        foreach (var model in models)
        {
            Models.Add(model);
        }

        var next = models.FirstOrDefault(m => m.IsAvailable && string.Equals(m.Id, previousId, StringComparison.Ordinal))
            ?? models.FirstOrDefault(m => m.IsAvailable && !m.IsAuto && m.IsLocal)
            ?? models.FirstOrDefault(m => m.IsAvailable && !m.IsAuto)
            ?? models.FirstOrDefault(m => m.IsAvailable);

        // Always raise: the old instance has left the list even when the id is the same.
        _selectedModel = null;
        SelectedModel = next;
        if (next is null)
        {
            OnPropertyChanged(nameof(SelectedModel));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        IsLoading = false;
        Message = models.Count == 0
            ? "Pillion Host lists no chat models. Add a provider under Tools > Options > Pillion Programmer > Providers and keys."
            : next is null ? "No model can be used right now; hover over a model to see why." : string.Empty;
    }

    /// <summary>Shows why the list could not be fetched, keeping any previous list. Call on the UI thread.</summary>
    /// <param name="message">The user-facing reason.</param>
    internal void SetError(string message)
    {
        IsLoading = false;
        Message = "Could not list models: " + message;
    }

    /// <summary>Shows that the host is not ready. Call on the UI thread.</summary>
    internal void SetWaiting()
    {
        IsLoading = false;
        if (Models.Count == 0)
        {
            Message = "Waiting for Pillion Host.";
        }
    }
}
