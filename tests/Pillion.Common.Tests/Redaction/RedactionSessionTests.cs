using System.Collections;
using System.Reflection;
using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

public class RedactionSessionTests
{
    private static string Aws(int variant) => SecretFactory.Create(SecretKind.AwsAccessKey, variant);

    [Fact]
    public void Redact_ReplacesSecretWithKindPlaceholder_AndRestoreReturnsOriginal()
    {
        using var session = new RedactionSession();
        var text = $"aws_access_key_id = {Aws(0)}\n";

        var redacted = session.Redact(text);

        Assert.Equal("aws_access_key_id = <redacted:aws-access-key>\n", redacted);
        Assert.Equal(text, session.Restore(redacted));
    }

    [Fact]
    public void Redact_TextWithoutSecrets_IsReturnedUnchanged()
    {
        using var session = new RedactionSession();

        Assert.Equal("nothing here", session.Redact("nothing here"));
        Assert.Equal(0, session.Redactions.Count);
    }

    [Fact]
    public void TwoDifferentValuesOfOneKind_GetNumberedPlaceholders()
    {
        using var session = new RedactionSession();
        var text = $"a={Aws(0)} b={Aws(1)}";

        var redacted = session.Redact(text);

        Assert.Equal("a=<redacted:aws-access-key#1> b=<redacted:aws-access-key#2>", redacted);
        Assert.Equal(text, session.Restore(redacted));
    }

    [Fact]
    public void SameValueTwice_GetsTheSamePlaceholder_AndIsCountedTwice()
    {
        using var session = new RedactionSession();
        var key = Aws(0);

        var redacted = session.Redact($"{key} and again {key}");

        Assert.Equal("<redacted:aws-access-key> and again <redacted:aws-access-key>", redacted);
        Assert.Equal(2, session.Redactions.Count);
        Assert.Equal(1, session.Redactions.DistinctCount);
    }

    [Fact]
    public void PlaceholdersAreStable_AcrossRedactCallsInOneSession()
    {
        using var session = new RedactionSession();
        var first = session.Redact($"one {Aws(0)}");
        var second = session.Redact($"two {Aws(1)} and {Aws(0)}");

        // The first value keeps the placeholder text the model has already seen.
        Assert.Equal("one <redacted:aws-access-key>", first);
        Assert.Equal("two <redacted:aws-access-key#2> and <redacted:aws-access-key>", second);
        Assert.Equal($"{Aws(0)} {Aws(1)}", session.Restore("<redacted:aws-access-key> <redacted:aws-access-key#2>"));
    }

    [Fact]
    public void ExplicitFirstPlaceholder_IsAcceptedForAnUnnumberedFirstValue()
    {
        using var session = new RedactionSession();
        session.Redact($"only {Aws(0)}");

        Assert.Equal(Aws(0), session.Restore("<redacted:aws-access-key#1>"));
        Assert.Empty(session.FindUnresolved("<redacted:aws-access-key#1>"));
    }

    [Fact]
    public void UnnumberedPlaceholder_IsUnresolved_WhenValuesWereNumbered()
    {
        using var session = new RedactionSession();
        session.Redact($"{Aws(0)} {Aws(1)}");

        // Ambiguous, so it must not be guessed.
        Assert.Equal(["<redacted:aws-access-key>"], session.FindUnresolved("use <redacted:aws-access-key> here"));
    }

    [Fact]
    public void DifferentKinds_NumberIndependently()
    {
        using var session = new RedactionSession();
        var jwt = SecretFactory.Create(SecretKind.Jwt, 0);

        var redacted = session.Redact($"{Aws(0)} {jwt} {Aws(1)}");

        Assert.Equal("<redacted:aws-access-key#1> <redacted:jwt> <redacted:aws-access-key#2>", redacted);
    }

    [Fact]
    public void FindUnresolved_ReportsUnknownPlaceholders_AndNotKnownOnes()
    {
        using var session = new RedactionSession();
        var redacted = session.Redact($"key={Aws(0)}");

        var unresolved = session.FindUnresolved($"{redacted} <redacted:bogus> <redacted:jwt> <redacted:bogus> <redacted:not a token>");

        Assert.Equal(["<redacted:bogus>", "<redacted:jwt>", "<redacted:not a token>"], unresolved);
        Assert.Empty(session.FindUnresolved(redacted));
        Assert.Empty(session.FindUnresolved("no placeholders here"));
    }

    [Fact]
    public void Restore_LeavesUnknownPlaceholdersAlone()
    {
        using var session = new RedactionSession();
        session.Redact($"key={Aws(0)}");

        Assert.Equal($"{Aws(0)} <redacted:bogus>", session.Restore("<redacted:aws-access-key> <redacted:bogus>"));
    }

    [Fact]
    public void Restore_IsSinglePass_SoOriginalsThatLookLikePlaceholdersAreNotExpandedAgain()
    {
        // A password that is itself placeholder-shaped is allow-listed and never enters the maps, so build the
        // situation with a legitimate secret and check the output is not re-scanned for placeholders.
        using var session = new RedactionSession();
        var key = Aws(0);
        session.Redact(key);

        Assert.Equal(key + key, session.Restore("<redacted:aws-access-key><redacted:aws-access-key>"));
    }

    [Fact]
    public void Placeholders_AreNotDifferentiatedByContent_ButMapToDifferentOriginalsAcrossSessions()
    {
        using var first = new RedactionSession();
        using var second = new RedactionSession();

        var fromFirst = first.Redact($"key={Aws(0)}");
        var fromSecond = second.Redact($"key={Aws(1)}");

        // Same text, different secrets: a placeholder is only meaningful in the session that issued it.
        Assert.Equal(fromFirst, fromSecond);
        Assert.Equal($"key={Aws(0)}", first.Restore(fromFirst));
        Assert.Equal($"key={Aws(1)}", second.Restore(fromFirst));
        Assert.DoesNotContain(Aws(0), second.Restore(fromFirst), StringComparison.Ordinal);
    }

    [Fact]
    public void PlaceholderFromAnotherSession_IsUnresolved_WhenTheKindWasNotRedactedHere()
    {
        using var first = new RedactionSession();
        using var second = new RedactionSession();
        var redacted = first.Redact($"token {SecretFactory.Create(SecretKind.Jwt, 0)}");

        Assert.Equal(redacted, second.Restore(redacted));
        Assert.Equal(["<redacted:jwt>"], second.FindUnresolved(redacted));
    }

    [Fact]
    public void Redactions_ExposeCountAndKindsButNeverOriginals()
    {
        using var session = new RedactionSession();
        var jwt = SecretFactory.Create(SecretKind.Jwt, 0);
        session.Redact($"{Aws(0)} {Aws(0)} {jwt}");

        var summary = session.Redactions;

        Assert.Equal(3, summary.Count);
        Assert.Equal(2, summary.DistinctCount);
        Assert.Equal(["aws-access-key", "jwt"], summary.Kinds);
        Assert.Equal(2, summary.CountsByKind["aws-access-key"]);
        Assert.Equal("3 redactions: aws-access-key, jwt", summary.Describe());
        Assert.Equal(summary.Describe(), summary.ToString());
        Assert.DoesNotContain(Aws(0), summary.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, summary.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_Wording_HandlesNoneAndOne()
    {
        using var session = new RedactionSession();
        Assert.Equal("No redactions", session.Redactions.Describe());

        session.Redact($"id {Aws(0)}");
        Assert.Equal("1 redaction: aws-access-key", session.Redactions.Describe());
    }

    [Fact]
    public void Dispose_ClearsOriginals_AndFurtherUseThrows()
    {
        var session = new RedactionSession();
        session.Redact($"id {Aws(0)} {Aws(1)}");

        session.Dispose();

        // The two maps that hold originals (as keys or values) must be empty; counts and kind ids may remain.
        foreach (var field in new[] { "_placeholderByValue", "_originalByPlaceholder" })
        {
            var map = (IEnumerable)typeof(RedactionSession).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            Assert.False(map.Cast<object>().Any(), $"{field} still holds data after Dispose");
        }

        Assert.Throws<ObjectDisposedException>(() => session.Redact("x"));
        Assert.Throws<ObjectDisposedException>(() => session.Restore("x"));
        Assert.Throws<ObjectDisposedException>(() => session.FindUnresolved("x"));
        Assert.Equal(2, session.Redactions.Count);
        session.Dispose();
    }

    [Fact]
    public void RedactingAlreadyRedactedText_ChangesNothing()
    {
        using var session = new RedactionSession();
        var once = session.Redact($"a={Aws(0)};password=" + SecretFactory.Create(SecretKind.ConnectionStringPassword, 0));

        Assert.Equal(once, session.Redact(once));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        using var session = new RedactionSession();

        Assert.Throws<ArgumentNullException>(() => session.Redact(null!));
        Assert.Throws<ArgumentNullException>(() => session.Restore(null!));
        Assert.Throws<ArgumentNullException>(() => session.FindUnresolved(null!));
    }

    [Fact]
    public async Task ConcurrentUse_ProducesOneConsistentMapping()
    {
        using var session = new RedactionSession();
        var text = $"a={Aws(0)} b={Aws(1)} c={Aws(2)} d={SecretFactory.Create(SecretKind.Jwt, 0)}";

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            var last = string.Empty;
            for (var i = 0; i < 50; i++)
            {
                last = session.Redact(text);
                Assert.Equal(text, session.Restore(last));
            }

            return last;
        })));

        Assert.Single(results.Distinct());
        Assert.Equal(4, session.Redactions.DistinctCount);
        Assert.Equal(16 * 50 * 4, session.Redactions.Count);
    }
}
