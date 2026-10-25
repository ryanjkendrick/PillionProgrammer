namespace Pillion.CommandRunner.Terminal;

/// <summary>The styling flags of a cell, set and cleared by SGR sequences.</summary>
[Flags]
public enum CellStyle : byte
{
    /// <summary>No styling.</summary>
    None = 0,

    /// <summary>Bold or increased intensity (SGR 1).</summary>
    Bold = 1,

    /// <summary>Faint or decreased intensity (SGR 2).</summary>
    Faint = 2,

    /// <summary>Italic (SGR 3).</summary>
    Italic = 4,

    /// <summary>Underline (SGR 4).</summary>
    Underline = 8,

    /// <summary>Reverse video (SGR 7).</summary>
    Inverse = 16,

    /// <summary>Strikethrough (SGR 9).</summary>
    Strikethrough = 32,
}

/// <summary>
/// A terminal colour: the default colour, one of the 256 indexed colours, or a 24-bit RGB colour. Colours are kept as
/// attributes on the screen buffer so the Terminal window can render them; the model's view is text only.
/// </summary>
/// <param name="Value">
/// The packed value: 0 for the default colour; <c>0x0100_0000 | index</c> for an indexed colour; <c>0x0200_0000 | rgb</c>
/// for an RGB colour.
/// </param>
public readonly record struct TerminalColor(uint Value)
{
    private const uint IndexedTag = 0x0100_0000;
    private const uint RgbTag = 0x0200_0000;

    /// <summary>The terminal's default foreground or background colour.</summary>
    public static TerminalColor Default => default;

    /// <summary>Gets a value indicating whether this is the default colour.</summary>
    public bool IsDefault => Value == 0;

    /// <summary>Gets a value indicating whether this is an indexed (0 to 255) colour.</summary>
    public bool IsIndexed => (Value & IndexedTag) != 0;

    /// <summary>Gets a value indicating whether this is a 24-bit colour.</summary>
    public bool IsRgb => (Value & RgbTag) != 0;

    /// <summary>Gets the index of an indexed colour (0 to 255), or 0 for other kinds.</summary>
    public int Index => IsIndexed ? (int)(Value & 0xFF) : 0;

    /// <summary>Gets the red component of an RGB colour (0 for other kinds).</summary>
    public byte R => (byte)((Value >> 16) & 0xFF);

    /// <summary>Gets the green component of an RGB colour (0 for other kinds).</summary>
    public byte G => (byte)((Value >> 8) & 0xFF);

    /// <summary>Gets the blue component of an RGB colour (0 for other kinds).</summary>
    public byte B => (byte)(Value & 0xFF);

    /// <summary>Makes an indexed colour.</summary>
    /// <param name="index">The index, 0 to 255; other values are clamped.</param>
    /// <returns>The colour.</returns>
    public static TerminalColor Indexed(int index) => new(IndexedTag | (uint)Math.Clamp(index, 0, 255));

    /// <summary>Makes a 24-bit colour.</summary>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    /// <returns>The colour.</returns>
    public static TerminalColor Rgb(int r, int g, int b) =>
        new(RgbTag | ((uint)Math.Clamp(r, 0, 255) << 16) | ((uint)Math.Clamp(g, 0, 255) << 8) | (uint)Math.Clamp(b, 0, 255));

    /// <inheritdoc/>
    public override string ToString() => IsDefault ? "default" : IsIndexed ? $"#{Index}" : $"rgb({R},{G},{B})";
}

/// <summary>The attributes of one cell: foreground, background and style.</summary>
/// <param name="Foreground">The foreground colour.</param>
/// <param name="Background">The background colour.</param>
/// <param name="Style">The style flags.</param>
public readonly record struct CellAttributes(TerminalColor Foreground, TerminalColor Background, CellStyle Style)
{
    /// <summary>The attributes of an untouched cell.</summary>
    public static CellAttributes Default => default;

    /// <summary>Gets a value indicating whether these are the default attributes.</summary>
    public bool IsDefault => Foreground.IsDefault && Background.IsDefault && Style == CellStyle.None;
}
