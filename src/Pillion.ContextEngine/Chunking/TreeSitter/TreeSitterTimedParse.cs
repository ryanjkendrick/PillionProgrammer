using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using TsParser = TreeSitter.Parser;
using TsTree = TreeSitter.Tree;

namespace Pillion.ContextEngine.Chunking.TreeSitter;

/// <summary>
/// Parses with a time cap (issue #101: some inputs keep the TSX grammar busy for minutes). The bindings expose neither the
/// runtime's timeout nor its cancellation flag, and the runtime they ship (0.25 or later) has dropped both for
/// <c>ts_parser_parse_with_options</c>, whose progress callback cancels the parse when it returns true. This class calls
/// that entry point itself, with the parser and tree handles the bindings keep internal, and the same UTF-16 input the
/// bindings' own <c>Parse</c> uses, so the tree is the one <see cref="TsParser.Parse(string)"/> would return. If the
/// bindings or the runtime ever lack what it needs, <see cref="IsAvailable"/> is false and the parse runs without a cap.
/// </summary>
internal static class TreeSitterTimedParse
{
    private const string ParseWithOptionsExport = "ts_parser_parse_with_options";
    private const string TreeDeleteExport = "ts_tree_delete";
    private const int Utf16LittleEndian = 1; // TSInputEncodingUTF16LE

    private static readonly ReadCallback s_read = Read;
    private static readonly ProgressCallback s_progress = Progress;
    private static readonly Lazy<Binding?> s_binding = new(Bind, LazyThreadSafetyMode.ExecutionAndPublication);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ParseWithOptionsFunction(IntPtr parser, IntPtr oldTree, TsInput input, TsParseOptions options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TreeDeleteFunction(IntPtr tree);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ReadCallback(IntPtr payload, uint byteIndex, TsPoint position, IntPtr bytesRead);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ProgressCallback(IntPtr state);

    /// <summary>Gets a value indicating whether parses can be capped on this machine.</summary>
    public static bool IsAvailable => s_binding.Value is not null;

    /// <summary>Parses <paramref name="text"/>; with a positive <paramref name="cap"/>, gives up once the parse has run that long.</summary>
    /// <param name="parser">A parser with its language set.</param>
    /// <param name="text">The text.</param>
    /// <param name="cap">The longest the parse may take; zero, negative or <see cref="Timeout.InfiniteTimeSpan"/> for no cap.</param>
    /// <param name="timedOut">Set when the parse was cancelled at the cap (the result is then null).</param>
    /// <returns>The tree, or null when the parser returned none or the cap was reached.</returns>
    public static TsTree? Parse(TsParser parser, string text, TimeSpan cap, out bool timedOut)
    {
        timedOut = false;
        if (cap <= TimeSpan.Zero || s_binding.Value is not { } binding)
        {
            return parser.Parse(text);
        }

        var budget = new Budget(Stopwatch.GetTimestamp() + (long)Math.Min(cap.TotalSeconds * Stopwatch.Frequency, long.MaxValue / 4));
        var pinned = GCHandle.Alloc(text, GCHandleType.Pinned);
        var input = GCHandle.Alloc(new InputState(pinned.AddrOfPinnedObject(), (uint)text.Length * 2));
        var progress = GCHandle.Alloc(budget);
        IntPtr native;
        try
        {
            native = binding.ParseWithOptions(
                binding.ParserHandle(parser),
                IntPtr.Zero,
                new TsInput { Payload = GCHandle.ToIntPtr(input), Read = binding.ReadPointer, Encoding = Utf16LittleEndian, Decode = IntPtr.Zero },
                new TsParseOptions { Payload = GCHandle.ToIntPtr(progress), ProgressCallback = binding.ProgressPointer });
        }
        finally
        {
            progress.Free();
            input.Free();
            pinned.Free();
        }

        if (native == IntPtr.Zero)
        {
            // Cancelled at the cap (or no tree at all); the parser keeps its state for a resume, which never comes.
            timedOut = budget.Expired;
            parser.Reset();
            return null;
        }

        try
        {
            return binding.CreateTree(native, text);
        }
        catch
        {
            binding.DeleteTree(native);
            throw;
        }
    }

    private static Binding? Bind()
    {
        try
        {
            TreeSitterNative.EnsureRegistered();
            var self = typeof(TsParser).GetProperty("Self", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetMethod;
            var treeConstructor = typeof(TsTree).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, [typeof(IntPtr), typeof(string)]);
            if (self is null || self.ReturnType != typeof(IntPtr) || treeConstructor is null)
            {
                return null;
            }

            // Through the bindings' assembly, so the import resolver TreeSitterNative registers finds the runtime library.
            if (!NativeLibrary.TryLoad("tree-sitter", typeof(TsParser).Assembly, null, out var library)
                || !NativeLibrary.TryGetExport(library, ParseWithOptionsExport, out var parse)
                || !NativeLibrary.TryGetExport(library, TreeDeleteExport, out var delete))
            {
                return null;
            }

            return new Binding(
                self.CreateDelegate<Func<TsParser, IntPtr>>(),
                (handle, text) => (TsTree)treeConstructor.Invoke([handle, text]),
                Marshal.GetDelegateForFunctionPointer<ParseWithOptionsFunction>(parse),
                Marshal.GetDelegateForFunctionPointer<TreeDeleteFunction>(delete),
                Marshal.GetFunctionPointerForDelegate(s_read),
                Marshal.GetFunctionPointerForDelegate(s_progress));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or MemberAccessException or TargetInvocationException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Hands the runtime the rest of the text from <paramref name="byteIndex"/> (the input is UTF-16, so bytes).</summary>
    private static IntPtr Read(IntPtr payload, uint byteIndex, TsPoint _, IntPtr bytesRead)
    {
        var state = (InputState)GCHandle.FromIntPtr(payload).Target!;
        var index = Math.Min(byteIndex, state.ByteLength);
        Marshal.WriteInt32(bytesRead, (int)(state.ByteLength - index));
        return state.Text + (int)index;
    }

    /// <summary>Called by the runtime every hundred or so parse operations: true (1) cancels the parse.</summary>
    private static byte Progress(IntPtr state)
    {
        // TSParseState starts with the payload the options carried.
        var budget = (Budget)GCHandle.FromIntPtr(Marshal.ReadIntPtr(state)).Target!;
        if (Stopwatch.GetTimestamp() <= budget.Deadline)
        {
            return 0;
        }

        budget.Expired = true;
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TsPoint
    {
        public uint Row;
        public uint Column;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TsInput
    {
        public IntPtr Payload;
        public IntPtr Read;
        public int Encoding;
        public IntPtr Decode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TsParseOptions
    {
        public IntPtr Payload;
        public IntPtr ProgressCallback;
    }

    private sealed record InputState(IntPtr Text, uint ByteLength);

    private sealed class Budget(long deadline)
    {
        public long Deadline { get; } = deadline;

        public bool Expired { get; set; }
    }

    private sealed record Binding(
        Func<TsParser, IntPtr> ParserHandle,
        Func<IntPtr, string, TsTree> CreateTree,
        ParseWithOptionsFunction ParseWithOptions,
        TreeDeleteFunction DeleteTree,
        IntPtr ReadPointer,
        IntPtr ProgressPointer);
}
