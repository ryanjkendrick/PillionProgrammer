namespace Pillion.Eval.Golden;

/// <summary>Builds a golden set's corpus: a copy of its source folders in a fresh folder the pipeline indexes.</summary>
internal static class Corpus
{
    /// <summary>Copies the set's corpus folders into a destination folder.</summary>
    /// <param name="set">The set.</param>
    /// <param name="destination">An empty folder that becomes the corpus root.</param>
    /// <returns>The corpus-relative paths of the copied files (forward slashes), sorted ordinally.</returns>
    /// <exception cref="GoldenSetException">A source folder does not exist.</exception>
    public static IReadOnlyList<string> Copy(GoldenSet set, string destination)
    {
        ArgumentNullException.ThrowIfNull(set);
        var excluded = new HashSet<string>(GoldenSet.DefaultExclude.Concat(set.Exclude), StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var source in set.Corpus)
        {
            var from = Path.GetFullPath(Path.Combine(set.Directory, source.Path));
            if (!Directory.Exists(from))
            {
                throw new GoldenSetException($"{set.Name}: the corpus folder {from} does not exist.");
            }

            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(from, file).Replace('\\', '/');
                if (relative.Split('/').SkipLast(1).Any(excluded.Contains))
                {
                    continue;
                }

                var target = source.As.Length == 0 ? relative : source.As + "/" + relative;
                var full = Path.Combine(destination, target.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.Copy(file, full, overwrite: true);
                files.Add(target);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>Lists the files a set's corpus would hold, without copying.</summary>
    /// <param name="set">The set.</param>
    /// <returns>The corpus-relative paths (forward slashes), sorted ordinally.</returns>
    public static IReadOnlyList<string> List(GoldenSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var excluded = new HashSet<string>(GoldenSet.DefaultExclude.Concat(set.Exclude), StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var source in set.Corpus)
        {
            var from = Path.GetFullPath(Path.Combine(set.Directory, source.Path));
            if (!Directory.Exists(from))
            {
                throw new GoldenSetException($"{set.Name}: the corpus folder {from} does not exist.");
            }

            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(from, file).Replace('\\', '/');
                if (!relative.Split('/').SkipLast(1).Any(excluded.Contains))
                {
                    files.Add(source.As.Length == 0 ? relative : source.As + "/" + relative);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>Checks that every expected file of every query is in the corpus, with the exact case.</summary>
    /// <param name="set">The set.</param>
    /// <param name="corpusFiles">The corpus files.</param>
    /// <returns>One message per problem; empty when the set is consistent.</returns>
    public static IReadOnlyList<string> CheckExpectedFiles(GoldenSet set, IReadOnlyList<string> corpusFiles)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(corpusFiles);
        var exact = new HashSet<string>(corpusFiles, StringComparer.Ordinal);
        var loose = corpusFiles.ToLookup(f => f, StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var query in set.Queries)
        {
            foreach (var file in query.ExpectedFiles)
            {
                if (exact.Contains(file))
                {
                    continue;
                }

                problems.Add(loose[file].FirstOrDefault() is { } other
                    ? $"{query.Id}: expected file '{file}' differs in case from '{other}'."
                    : $"{query.Id}: expected file '{file}' is not in the corpus.");
            }
        }

        return problems;
    }
}
