using System.Globalization;
using Pillion.Common.Settings;

namespace Pillion.Host.InlineEdit;

/// <summary>
/// The tunable limits of inline edits. <see cref="WholeScopeMaxLines"/> can be set in <c>settings.json</c> as
/// <c>inlineEdit.wholeScopeMaxLines</c> and is read again for every request, so a change applies to the next edit.
/// </summary>
internal sealed class InlineEditOptions
{
    /// <summary>The default for <see cref="WholeScopeMaxLines"/>.</summary>
    public const int DefaultWholeScopeMaxLines = 80;

    /// <summary>The key path of <see cref="WholeScopeMaxLines"/> in <c>settings.json</c>.</summary>
    public const string WholeScopeMaxLinesKey = "inlineEdit.wholeScopeMaxLines";

    private readonly Func<string, string?>? _readSetting;
    private int _wholeScopeMaxLines = DefaultWholeScopeMaxLines;

    /// <summary>Initializes a new instance of the <see cref="InlineEditOptions"/> class.</summary>
    /// <param name="readSetting">Reads a setting by its key path as JSON text (<see langword="null"/> when it is not set); <see langword="null"/> to use only the defaults.</param>
    public InlineEditOptions(Func<string, string?>? readSetting = null) => _readSetting = readSetting;

    /// <summary>
    /// Gets or sets the most lines a scope (or the proposal being refined) can have for the model to be asked to rewrite it
    /// whole, as plain code; larger scopes are edited with search-and-replace blocks. 0 always uses blocks. A value in
    /// <c>settings.json</c> that is not a whole number of 0 or more is ignored. Default 80.
    /// </summary>
    public int WholeScopeMaxLines
    {
        get => ReadSetting() ?? _wholeScopeMaxLines;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _wholeScopeMaxLines = value;
        }
    }

    private int? ReadSetting()
    {
        if (_readSetting is null)
        {
            return null;
        }

        try
        {
            return _readSetting(WholeScopeMaxLinesKey) is { } json
                && int.TryParse(json, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is SettingsException or InvalidOperationException or IOException or ArgumentException)
        {
            // An unreadable settings file means the default; the settings store reports its own problems.
            return null;
        }
    }
}
