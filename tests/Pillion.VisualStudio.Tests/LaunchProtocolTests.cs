using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

public class LaunchProtocolTests
{
    private static readonly Regex Base64UrlNoPadding = new("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant);

    [Fact]
    public void GenerateToken_Is43Base64UrlCharactersWithoutPadding()
    {
        for (var i = 0; i < 200; i++)
        {
            var token = LaunchProtocol.GenerateToken();

            Assert.Equal(43, token.Length);
            Assert.Matches(Base64UrlNoPadding, token);
            Assert.DoesNotContain('=', token);
            Assert.True(LaunchProtocol.IsWellFormedToken(token));
        }
    }

    [Fact]
    public void GenerateToken_Decodes_To32Bytes()
    {
        var token = LaunchProtocol.GenerateToken();
        var base64 = token.Replace('-', '+').Replace('_', '/') + "=";

        Assert.Equal(32, Convert.FromBase64String(base64).Length);
    }

    [Fact]
    public void GenerateToken_IsDifferentEveryTime()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => LaunchProtocol.GenerateToken()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void GenerateToken_UsesTheWholeBase64UrlAlphabet()
    {
        var seen = new HashSet<char>();
        for (var i = 0; i < 2000; i++)
        {
            seen.UnionWith(LaunchProtocol.GenerateToken());
        }

        Assert.Contains('-', seen);
        Assert.Contains('_', seen);
        Assert.DoesNotContain('+', seen);
        Assert.DoesNotContain('/', seen);
        Assert.Equal(64, seen.Count);
    }

    [Theory]
    [InlineData(new byte[] { 0xfb, 0xff }, "-_8")]
    [InlineData(new byte[] { 0x00 }, "AA")]
    [InlineData(new byte[] { 0x66, 0x6f, 0x6f, 0x62, 0x61 }, "Zm9vYmE")]
    public void Base64UrlEncode_MatchesRfc4648(byte[] bytes, string expected) =>
        Assert.Equal(expected, LaunchProtocol.Base64UrlEncode(bytes));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void IsWellFormedToken_RejectsOtherShapes(string? token) =>
        Assert.False(LaunchProtocol.IsWellFormedToken(token));

    [Fact]
    public void CreatePipeName_IsPillionPidAndEightHexDigits()
    {
        var name = LaunchProtocol.CreatePipeName(4242);

        Assert.Matches(new Regex("^pillion-4242-[0-9a-f]{8}$", RegexOptions.CultureInvariant), name);
    }

    [Fact]
    public void CreatePipeName_IsRandom()
    {
        var names = Enumerable.Range(0, 200).Select(_ => LaunchProtocol.CreatePipeName(1)).ToList();

        Assert.True(names.Distinct(StringComparer.Ordinal).Count() > 195);
    }

    [Fact]
    public void BuildArguments_FollowsTheProtocolOrder()
    {
        var args = LaunchProtocol.BuildArguments("pillion-1-0a1b2c3d", 1234, "5678", @"C:\Users\Some One\AppData\Local\PillionProgrammer\logs\");

        Assert.Equal(
            ["--pipe", "pillion-1-0a1b2c3d", "--parent-pid", "1234", "--token-handle", "5678", "--log-dir", @"C:\Users\Some One\AppData\Local\PillionProgrammer\logs"],
            args);
    }

    [Fact]
    public void BuildArguments_OmitsLogDirWhenNotGiven()
    {
        var args = LaunchProtocol.BuildArguments("p", 1, "2", logDirectory: null);

        Assert.DoesNotContain("--log-dir", args);
        Assert.Equal(6, args.Count);
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("with space")]
    [InlineData(@"C:\Program Files\x\")]
    [InlineData(@"quote""inside")]
    [InlineData(@"back\\slashes\\""and quote")]
    [InlineData("")]
    [InlineData(@"trailing\")]
    public void JoinArguments_RoundTripsThroughCommandLineToArgvW(string argument)
    {
        string[] original = ["first", argument, "--last"];

        var parsed = SplitCommandLine("prog.exe " + LaunchProtocol.JoinArguments(original));

        Assert.Equal(original, parsed.Skip(1));
    }

    private static string[] SplitCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
