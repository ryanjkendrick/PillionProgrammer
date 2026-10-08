namespace Pillion.Common.Settings;

/// <summary>The base type for errors raised by the settings store.</summary>
public class SettingsException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SettingsException"/> class.</summary>
    public SettingsException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SettingsException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public SettingsException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SettingsException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public SettingsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The settings file, a key path or a value is not valid.</summary>
public sealed class SettingsValidationException : SettingsException
{
    /// <summary>Initializes a new instance of the <see cref="SettingsValidationException"/> class.</summary>
    public SettingsValidationException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SettingsValidationException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public SettingsValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SettingsValidationException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public SettingsValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The settings file was written by a newer version of Pillion than this one and is refused.</summary>
public sealed class SettingsVersionException : SettingsException
{
    /// <summary>Initializes a new instance of the <see cref="SettingsVersionException"/> class.</summary>
    public SettingsVersionException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SettingsVersionException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public SettingsVersionException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SettingsVersionException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public SettingsVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SettingsVersionException"/> class.</summary>
    /// <param name="fileVersion">The version found in the file.</param>
    /// <param name="supportedVersion">The newest version this build understands.</param>
    public SettingsVersionException(int fileVersion, int supportedVersion)
        : base($"settings.json is schema version {fileVersion}, which is newer than the version {supportedVersion} this version of Pillion understands. Update Pillion; the file was not changed.")
    {
        FileVersion = fileVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>Gets the version found in the file, or 0 if unknown.</summary>
    public int FileVersion { get; }

    /// <summary>Gets the newest version this build understands, or 0 if unknown.</summary>
    public int SupportedVersion { get; }
}
