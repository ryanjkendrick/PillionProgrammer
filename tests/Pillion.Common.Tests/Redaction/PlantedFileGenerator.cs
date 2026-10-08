using System.Text;
using Pillion.Common.Redaction;

namespace Pillion.Common.Tests.Redaction;

/// <summary>The file formats the round-trip test plants secrets into.</summary>
public enum FileFormat
{
    Code,
    Json,
    Yaml,
    Env,
    Xml,
    ConnectionString,
}

/// <summary>A generated file plus what the test needs to check about it.</summary>
/// <param name="Text">The file content.</param>
/// <param name="Needles">Substrings (secret values, or one body line of a private key) that must not survive redaction.</param>
/// <param name="FillerLines">Benign lines that look vaguely secret-like (SHAs, GUIDs, base64) and must survive untouched.</param>
internal sealed record PlantedFile(string Text, IReadOnlyList<string> Needles, IReadOnlyList<string> FillerLines);

/// <summary>
/// Generates deterministic files in several formats with fabricated secrets of every <see cref="SecretKind"/>
/// planted in format-appropriate syntax, some of them twice and some kinds with two distinct values, mixed with
/// benign lines, and one unknown <c>&lt;redacted:bogus&gt;</c> token.
/// </summary>
internal static class PlantedFileGenerator
{
    public const string Bogus = "<redacted:bogus>";

    public static PlantedFile Generate(FileFormat format, int seed, int variantBase = 0)
    {
        var rng = new Random((seed * 977) + ((int)format * 131) + 7);
        var needles = new List<string>();
        var entries = new List<string>();

        foreach (var kind in Enum.GetValues<SecretKind>())
        {
            var first = rng.Next(0, 6);
            var variants = new List<int> { first };
            if (rng.Next(3) == 0)
            {
                variants.Add(first + 1 + rng.Next(5)); // a second distinct value of the same kind
            }

            variants.Add(first); // and the first value again, so the same original appears twice

            foreach (var variant in variants)
            {
                entries.Add(Entry(format, kind, variant + variantBase, rng, needles));
            }
        }

        // Shuffle deterministically so kinds are interleaved differently per file.
        for (var i = entries.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (entries[i], entries[j]) = (entries[j], entries[i]);
        }

        var filler = FillerFor(format, rng);
        var lines = new List<string>(entries);
        foreach (var line in filler)
        {
            lines.Insert(rng.Next(lines.Count + 1), line);
        }

        lines.Insert(rng.Next(lines.Count + 1), BogusLine(format));

        var text = Compose(format, lines);
        if (seed % 2 == 1)
        {
            text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        return new PlantedFile(text, needles.Distinct().ToList(), filler);
    }

    private static string Entry(FileFormat format, SecretKind kind, int variant, Random rng, List<string> needles)
    {
        var value = SecretFactory.Create(kind, variant);

        if (kind == SecretKind.GcpServiceAccount)
        {
            var keyId = SecretFactory.GcpKeyId(variant);
            needles.Add(keyId);
            needles.Add(SecretFactory.Needle(kind, value));
            var json = "{\"type\":\"service_account\",\"private_key_id\":\"" + keyId + "\",\"private_key\":\"" + value
                + "\",\"client_email\":\"svc@demo.iam.gserviceaccount.com\"}";
            return format switch
            {
                FileFormat.Code => $"    private const string ServiceAccount = \"\"\"{json}\"\"\";",
                FileFormat.Json => $"  \"serviceAccount\": {json},",
                FileFormat.Yaml => $"serviceAccount: '{json}'",
                FileFormat.Env => $"GOOGLE_CREDENTIALS='{json}'",
                FileFormat.Xml => $"    <add key=\"gcp\" value='{json}' />",
                _ => $"Server=db;Credentials='{json}';",
            };
        }

        if (kind == SecretKind.PrivateKeyBlock)
        {
            needles.Add(SecretFactory.Needle(kind, value));
            var escaped = value.Replace("\n", "\\n", StringComparison.Ordinal);
            return format switch
            {
                FileFormat.Code => "    private const string Pem = \"\"\"\n" + Indent(value, "        ") + "\n        \"\"\";",
                FileFormat.Json => $"  \"privateKey\": \"{escaped}\",",
                FileFormat.Yaml => "privateKey: |\n" + Indent(value, "  "),
                FileFormat.Env => $"PRIVATE_KEY=\"{escaped}\"",
                FileFormat.Xml => "    <PrivateKey>\n" + value + "\n    </PrivateKey>",
                _ => $"Server=db;PrivateKey='{escaped}';",
            };
        }

        needles.Add(value);
        var (name, shaped) = kind switch
        {
            SecretKind.AwsSecretKey => ("aws_secret_access_key", value),
            SecretKind.AzureStorageKey => ("storageAccountKey", value),
            SecretKind.GenericHighEntropy => ("serviceApiToken", value),
            SecretKind.ConnectionStringPassword => ("dbUrl", $"postgres://svc:{value}@db.example.net:5432/app"),
            SecretKind.AzureConnectionString => ("storageConnection", $"DefaultEndpointsProtocol=https;AccountName=demo;AccountKey={value};EndpointSuffix=core.windows.net"),
            _ => ("payload" + rng.Next(3), value),
        };

        if (format == FileFormat.ConnectionString)
        {
            return kind switch
            {
                SecretKind.ConnectionStringPassword => $"Server=db;Database=app;Password={value};Encrypt=True;",
                SecretKind.AzureConnectionString => shaped,
                _ => $"Server=db;Database=app;{name}={value};Encrypt=True;",
            };
        }

        return format switch
        {
            FileFormat.Code => $"    private const string {name} = \"{shaped}\";",
            FileFormat.Json => $"  \"{name}\": \"{shaped}\",",
            FileFormat.Yaml => rng.Next(2) == 0 ? $"{name}: \"{shaped}\"" : $"{name}: {shaped}",
            FileFormat.Env => $"{name.ToUpperInvariant()}={shaped}",
            _ => $"    <add key=\"{name}\" value=\"{shaped}\" />",
        };
    }

    private static string Indent(string text, string indent) =>
        string.Join("\n", text.Split('\n').Select(line => indent + line));

    private static List<string> FillerFor(FileFormat format, Random rng)
    {
        var sha = SecretFactory.Pick(rng, "0123456789abcdef", 40);
        var guid = DeterministicGuid.Next(rng).ToString();
        var image = SecretFactory.Pick(rng, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/", 120);
        return format switch
        {
            FileFormat.Code => [$"    private const string Commit = \"{sha}\";", $"    private static readonly Guid Id = Guid.Parse(\"{guid}\");", $"    private const string Icon = \"data:image/png;base64,{image}==\";"],
            FileFormat.Json => [$"  \"commit\": \"{sha}\",", $"  \"id\": \"{guid}\",", $"  \"icon\": \"data:image/png;base64,{image}==\","],
            FileFormat.Yaml => [$"commit: {sha}", $"id: {guid}", $"icon: data:image/png;base64,{image}=="],
            FileFormat.Env => [$"GIT_SHA={sha}", $"INSTANCE_ID={guid}", "LOG_LEVEL=Information"],
            FileFormat.Xml => [$"    <add key=\"Commit\" value=\"{sha}\" />", $"    <add key=\"Id\" value=\"{guid}\" />", $"    <add key=\"Icon\" value=\"data:image/png;base64,{image}==\" />"],
            _ => [$"Application Name=Demo;Commit={sha};", $"Application Name=Demo;InstanceId={guid};", "Server=db;Database=app;Encrypt=True;"],
        };
    }

    private static string BogusLine(FileFormat format) => format switch
    {
        FileFormat.Code => $"    // TODO restore {Bogus} before release",
        FileFormat.Json => $"  \"note\": \"see {Bogus}\",",
        FileFormat.Yaml => $"# {Bogus}",
        FileFormat.Env => $"# {Bogus}",
        FileFormat.Xml => $"    <!-- {Bogus} -->",
        _ => $"Server=db;Note={Bogus};",
    };

    private static string Compose(FileFormat format, List<string> lines)
    {
        var builder = new StringBuilder();
        switch (format)
        {
            case FileFormat.Code:
                builder.Append("namespace Demo;\n\npublic static class Config\n{\n");
                builder.AppendJoin("\n", lines).Append("\n}\n");
                break;
            case FileFormat.Json:
                builder.Append("{\n  \"app\": \"demo\",\n");
                builder.AppendJoin("\n", lines).Append("\n  \"last\": 1\n}\n");
                break;
            case FileFormat.Xml:
                builder.Append("<?xml version=\"1.0\"?>\n<configuration>\n  <appSettings>\n");
                builder.AppendJoin("\n", lines).Append("\n  </appSettings>\n</configuration>\n");
                break;
            default:
                builder.AppendJoin("\n", lines).Append('\n');
                break;
        }

        return builder.ToString();
    }
}

/// <summary>Deterministic GUID helper.</summary>
internal static class DeterministicGuid
{
    public static Guid Next(Random rng)
    {
        var bytes = new byte[16];
        rng.NextBytes(bytes);
        return new Guid(bytes);
    }
}
