using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// Replaces the host's model service for autocomplete requests. Meant for the Visual Studio integration tests, which run
/// inside devenv.exe and have no model (the same approach as <see cref="InlineChat.InlineChatOverrides"/>): the tests put
/// a canned completion service here, like <c>tests/Pillion.FakeHost</c>'s. <see langword="null"/> (the default) uses
/// Pillion Host.
/// </summary>
public static class AutocompleteOverrides
{
    private static volatile IModelService? _modelService;

    /// <summary>Gets or sets the model service autocomplete requests use instead of the host's, or <see langword="null"/>.</summary>
    public static IModelService? ModelService
    {
        get => _modelService;
        set => _modelService = value;
    }
}
