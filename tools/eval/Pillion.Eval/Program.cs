using System.Globalization;
using Pillion.Eval.Golden;
using Pillion.Eval.Models;
using Pillion.Eval.Retrieval;

namespace Pillion.Eval;

/// <summary>The evaluation harness's command line.</summary>
internal static class Program
{
    /// <summary>The usage text.</summary>
    internal const string Usage = """
        Pillion evaluation harness (design section 15).

        Pillion.Eval retrieval --set <folder> [--set <folder>...] --model <spec> [--model <spec>...] [options]
          Indexes each golden set's corpus with each model through the host's real pipeline (Local-only, a temporary
          encrypted index), runs every query through the retrieval service in each mode and reports recall at 10, MRR,
          latency and the int8 loss against float32. Output: tools/eval/out/<timestamp>/ (summary.md, one .json and .md
          per set and model).
          Model specs: bundled/bge-small-en-v1.5, bundled/jina-embeddings-v2-base-code, bundled/multilingual-e5-small,
                       ollama/qwen3-embedding:0.6b, ollama/nomic-embed-text (any Ollama embedding model).
          --modes hybrid,lexical,vector   modes to score (also: symbol)
          --no-int8-loss                  skip the int8 loss measurement
          --budget-ms <n>                 time budget per search (default 60000; the product default is 300)
          --out <folder>                  output root (default tools/eval/out in this repository)
          --models-dir <folder>           where on-request bundled models are downloaded
                                          (default %LOCALAPPDATA%\PillionProgrammer-eval\models)
          --no-download                   fail instead of downloading a missing bundled model
          --ollama <url>                  Ollama endpoint (default http://localhost:11434/)
          --directml                      let bundled models run on DirectML (the baseline is the CPU)
          --index-timeout-min <n>         longest indexing may take (default 180)
          --keep-index                    keep the temporary corpus and index
          --work-dir <folder>             keep each set and model's corpus and index under this folder; running
                                          again with the same folder resumes a stopped run
          --allow-missing-files           run even when an expected file is not in the corpus
          --verbose                       show host logs at Information level

        Pillion.Eval validate --set <folder> [--set <folder>...]
          Checks that the set parses and every expected file is in its corpus (no model, no index).
        """;

    /// <summary>Runs the harness.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>0 on success, 1 on failure, 2 on a usage error.</returns>
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var parsed = CommandLine.Parse(args);
            switch (parsed.Command)
            {
                case "retrieval":
                    var options = parsed.ToRetrievalOptions();
                    var report = await RetrievalRunner.RunAsync(options, Console.Out, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine();
                    Console.WriteLine(await File.ReadAllTextAsync(report.SummaryPath, cancellation.Token).ConfigureAwait(false));
                    return 0;
                case "validate":
                    return Validate(parsed.Sets);
                default:
                    Console.Error.WriteLine($"Unknown command '{parsed.Command}'.");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run Pillion.Eval --help for the usage.");
            return 2;
        }
        catch (Exception ex) when (ex is GoldenSetException or InvalidOperationException or IOException or TimeoutException or HttpRequestException or Contracts.PillionRpcException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
    }

    private static int Validate(List<string> sets)
    {
        if (sets.Count == 0)
        {
            throw new FormatException("validate needs at least one --set.");
        }

        var failed = false;
        foreach (var folder in sets)
        {
            var set = GoldenSet.Load(folder);
            var corpus = Corpus.List(set);
            var problems = Corpus.CheckExpectedFiles(set, corpus);
            var kinds = string.Join(", ", set.Queries.GroupBy(q => q.Kind ?? "(none)").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} {g.Count()}")));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{set.Name}: {set.Queries.Count} queries ({kinds}), {set.Queries.Count(q => q.ExpectedSymbols.Count > 0)} with symbols, corpus {corpus.Count} files."));
            foreach (var problem in problems)
            {
                Console.WriteLine("  " + problem);
            }

            failed |= problems.Count > 0;
        }

        return failed ? 1 : 0;
    }
}

/// <summary>The parsed command line.</summary>
internal sealed class CommandLine
{
    private CommandLine(string command) => Command = command;

    /// <summary>Gets the command.</summary>
    public string Command { get; }

    /// <summary>Gets the set folders.</summary>
    public List<string> Sets { get; } = [];

    /// <summary>Gets the model specs.</summary>
    public List<ModelSpec> Models { get; } = [];

    /// <summary>Gets the modes.</summary>
    public List<string> Modes { get; private set; } = ["hybrid", "lexical", "vector"];

    /// <summary>Gets the output root, or <see langword="null"/> for the default.</summary>
    public string? Out { get; private set; }

    /// <summary>Gets the work folder, or <see langword="null"/> for a temporary one.</summary>
    public string? WorkDirectory { get; private set; }

    /// <summary>Gets the models folder, or <see langword="null"/> for the default.</summary>
    public string? ModelsDirectory { get; private set; }

    /// <summary>Gets the flags that were given.</summary>
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the search budget.</summary>
    public TimeSpan Budget { get; private set; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the Ollama endpoint.</summary>
    public string Ollama { get; private set; } = "http://localhost:11434/";

    /// <summary>Gets the indexing timeout.</summary>
    public TimeSpan IndexTimeout { get; private set; } = TimeSpan.FromMinutes(180);

    /// <summary>Parses the arguments.</summary>
    /// <param name="args">The arguments, command first.</param>
    /// <returns>The parsed command line.</returns>
    /// <exception cref="FormatException">An option is unknown or lacks its value.</exception>
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = new CommandLine(args[0]);
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new FormatException($"{arg} needs a value.");
            switch (arg)
            {
                case "--set":
                    result.Sets.Add(Value());
                    break;
                case "--model":
                    result.Models.Add(ModelSpec.Parse(Value()));
                    break;
                case "--modes":
                    result.Modes = [.. Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(m => m.ToLowerInvariant())];
                    foreach (var mode in result.Modes)
                    {
                        RetrievalRunner.ParseMode(mode);
                    }

                    break;
                case "--out":
                    result.Out = Value();
                    break;
                case "--work-dir":
                    result.WorkDirectory = Value();
                    break;
                case "--models-dir":
                    result.ModelsDirectory = Value();
                    break;
                case "--budget-ms":
                    result.Budget = TimeSpan.FromMilliseconds(PositiveNumber(arg, Value()));
                    break;
                case "--index-timeout-min":
                    result.IndexTimeout = TimeSpan.FromMinutes(PositiveNumber(arg, Value()));
                    break;
                case "--ollama":
                    var endpoint = Value();
                    result.Ollama = endpoint.EndsWith('/') ? endpoint : endpoint + "/";
                    break;
                case "--no-int8-loss" or "--no-download" or "--directml" or "--keep-index" or "--allow-missing-files" or "--verbose":
                    result.Flags.Add(arg);
                    break;
                default:
                    throw new FormatException($"Unknown option '{arg}'.");
            }
        }

        return result;
    }

    /// <summary>Builds the retrieval options.</summary>
    /// <returns>The options.</returns>
    /// <exception cref="FormatException">No set or no model was given.</exception>
    public RetrievalRunOptions ToRetrievalOptions()
    {
        if (Sets.Count == 0 || Models.Count == 0 || Modes.Count == 0)
        {
            throw new FormatException("retrieval needs at least one --set, one --model and one mode.");
        }

        return new RetrievalRunOptions
        {
            Sets = Sets,
            Models = Models,
            Modes = Modes,
            OutputRoot = Out ?? DefaultOutputRoot(),
            Int8Loss = !Flags.Contains("--no-int8-loss"),
            Budget = Budget,
            ModelsDirectory = ModelsDirectory ?? DefaultModelsDirectory(),
            Download = !Flags.Contains("--no-download"),
            OllamaEndpoint = Ollama,
            DirectML = Flags.Contains("--directml"),
            KeepIndex = Flags.Contains("--keep-index"),
            WorkDirectory = WorkDirectory,
            IndexTimeout = IndexTimeout,
            Verbose = Flags.Contains("--verbose"),
            AllowMissingFiles = Flags.Contains("--allow-missing-files"),
        };
    }

    /// <summary>Finds the repository root above a folder (the folder holding <c>PillionProgrammer.NoVsix.slnf</c>).</summary>
    /// <param name="start">Where to start.</param>
    /// <returns>The root, or <see langword="null"/>.</returns>
    public static string? FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PillionProgrammer.NoVsix.slnf")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>The default output root: <c>tools/eval/out</c> in the repository around the working folder or the harness.</summary>
    /// <returns>The folder.</returns>
    public static string DefaultOutputRoot()
    {
        var root = FindRepositoryRoot(Environment.CurrentDirectory) ?? FindRepositoryRoot(AppContext.BaseDirectory);
        return root is null ? Path.Combine(Environment.CurrentDirectory, "out") : Path.Combine(root, "tools", "eval", "out");
    }

    /// <summary>The default folder for downloaded models, apart from the product's own models folder.</summary>
    /// <returns>The folder.</returns>
    public static string DefaultModelsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PillionProgrammer-eval", "models");

    private static double PositiveNumber(string option, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new FormatException($"{option} needs a positive number (got '{value}').");
}
