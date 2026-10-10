using System;
using System.Globalization;

namespace Pillion.VisualStudio.Core.Signals;

/// <summary>One row of Visual Studio's table-based Error List, read by column name.</summary>
public interface IErrorListRow
{
    /// <summary>Gets the value of a column.</summary>
    /// <param name="keyName">The column's key name (see <see cref="TableKeys"/>).</param>
    /// <param name="value">The value, when the row has one.</param>
    /// <returns><see langword="true"/> when the row has a value for the column.</returns>
    bool TryGetValue(string keyName, out object? value);
}

/// <summary>
/// The Error List column key names the extension reads. They are the values of the same-named constants in Visual
/// Studio's <c>StandardTableKeyNames</c> (Pillion.VisualStudio.Core cannot reference the VS SDK); the VSIX checks at
/// start-up that they still match.
/// </summary>
public static class TableKeys
{
    /// <summary>The full path of the document (<c>StandardTableKeyNames.DocumentName</c>).</summary>
    public const string DocumentName = "documentname";

    /// <summary>The 0-based line (<c>StandardTableKeyNames.Line</c>).</summary>
    public const string Line = "line";

    /// <summary>The 0-based column (<c>StandardTableKeyNames.Column</c>).</summary>
    public const string Column = "column";

    /// <summary>The severity, a <c>__VSERRORCATEGORY</c> value (<c>StandardTableKeyNames.ErrorSeverity</c>).</summary>
    public const string ErrorSeverity = "errorseverity";

    /// <summary>The diagnostic code (<c>StandardTableKeyNames.ErrorCode</c>).</summary>
    public const string ErrorCode = "errorcode";

    /// <summary>The message (<c>StandardTableKeyNames.Text</c>).</summary>
    public const string Text = "text";

    /// <summary>The project name (<c>StandardTableKeyNames.ProjectName</c>).</summary>
    public const string ProjectName = "projectname";
}

/// <summary>Maps Error List rows to <see cref="DiagnosticInfo"/>.</summary>
public static class ErrorListMapper
{
    /// <summary>Maps one row.</summary>
    /// <param name="entry">The row.</param>
    /// <returns>The diagnostic, or <see langword="null"/> when the row has neither a message nor a code.</returns>
    public static DiagnosticInfo? Map(IErrorListRow entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        var message = Text(entry, TableKeys.Text);
        var code = Text(entry, TableKeys.ErrorCode);
        if (message is null && code is null)
        {
            return null;
        }

        return new DiagnosticInfo(
            Text(entry, TableKeys.DocumentName),
            OneBased(entry, TableKeys.Line),
            OneBased(entry, TableKeys.Column),
            MapSeverity(entry),
            code,
            message ?? string.Empty,
            Text(entry, TableKeys.ProjectName),
            DiagnosticSource.ErrorList);
    }

    private static IdeSeverity MapSeverity(IErrorListRow entry)
    {
        if (!entry.TryGetValue(TableKeys.ErrorSeverity, out var value) || value is null)
        {
            return IdeSeverity.Info;
        }

        // The table stores __VSERRORCATEGORY (a COM enum): EC_ERROR = 0, EC_WARNING = 1, EC_MESSAGE = 2.
        return ToInt(value) switch
        {
            0 => IdeSeverity.Error,
            1 => IdeSeverity.Warning,
            _ => IdeSeverity.Info,
        };
    }

    /// <summary>The table's lines and columns are 0-based; a missing or negative value means unknown (0).</summary>
    private static int OneBased(IErrorListRow entry, string key) =>
        entry.TryGetValue(key, out var value) && value is not null && ToInt(value) is var number && number >= 0
            ? number + 1
            : 0;

    private static string? Text(IErrorListRow entry, string key) =>
        entry.TryGetValue(key, out var value) && value is not null && value.ToString() is { Length: > 0 } text ? text : null;

    private static int ToInt(object value)
    {
        try
        {
            return value is IConvertible convertible ? convertible.ToInt32(CultureInfo.InvariantCulture) : -1;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return -1;
        }
    }
}
