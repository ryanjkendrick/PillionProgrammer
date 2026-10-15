using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Pillion.ModelGateway.Providers.Bundled;

/// <summary>The execution provider a loaded model runs on.</summary>
public enum LoadedExecutionProvider
{
    /// <summary>The CPU execution provider.</summary>
    Cpu,

    /// <summary>The DirectML execution provider.</summary>
    DirectML,
}

/// <summary>
/// One embedding model loaded into ONNX Runtime: tokenises, runs the graph in runs that stay within the attention budget,
/// pools (CLS or masked mean, as the model card says) and normalises. Inference is serialised per model; the gateway's
/// Embedding lane already sends one batch at a time.
/// </summary>
internal sealed class OnnxEmbeddingModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _hasTokenTypes;
    private readonly string[] _outputNames;
    private readonly long _maxAttentionCells;
    private bool _disposed;

    private OnnxEmbeddingModel(LocatedModel located, EmbeddingTokenizer tokenizer, InferenceSession session, LoadedExecutionProvider provider, string graph, long maxAttentionCells)
    {
        Located = located;
        Tokenizer = tokenizer;
        _session = session;
        ExecutionProvider = provider;
        Graph = graph;
        _maxAttentionCells = Math.Max(1, maxAttentionCells);
        _hasTokenTypes = session.InputMetadata.ContainsKey("token_type_ids");
        _outputNames = [session.OutputMetadata.Keys.First()];
        if (!session.InputMetadata.ContainsKey("input_ids") || !session.InputMetadata.ContainsKey("attention_mask"))
        {
            throw new InvalidOperationException($"The ONNX graph of {located.Model.Id} does not take input_ids and attention_mask.");
        }
    }

    /// <summary>Gets where the model's files are.</summary>
    public LocatedModel Located { get; }

    /// <summary>Gets the manifest entry.</summary>
    public ManifestModel Model => Located.Model;

    /// <summary>Gets the tokenizer.</summary>
    public EmbeddingTokenizer Tokenizer { get; }

    /// <summary>Gets the execution provider the session runs on.</summary>
    public LoadedExecutionProvider ExecutionProvider { get; }

    /// <summary>Gets the file name of the graph that was loaded.</summary>
    public string Graph { get; }

    /// <summary>
    /// Loads a model. The graph's bytes are read, checked against the manifest and handed to ONNX Runtime from memory, so the
    /// graph that runs is exactly the one that was verified.
    /// </summary>
    /// <param name="located">Where the model is.</param>
    /// <param name="tokenizer">The model's tokenizer.</param>
    /// <param name="useDirectML">Whether to create a DirectML session (with the FP16 graph when there is one).</param>
    /// <param name="options">The provider options.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ModelVerificationException">The graph does not match the manifest.</exception>
    /// <exception cref="OnnxRuntimeException">ONNX Runtime refused the graph or the execution provider.</exception>
    public static OnnxEmbeddingModel Load(LocatedModel located, EmbeddingTokenizer tokenizer, bool useDirectML, BundledModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(located);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(options);

        var file = useDirectML && located.HasFp16 ? located.Model.Find(ModelFileRole.ModelFp16)! : located.Model.Find(ModelFileRole.Model)!;
        var bytes = ModelFiles.ReadVerified(located.PathOf(file), file);
        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, options.IntraOpThreads),
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };

        // Idle intra-op threads sleep instead of spinning, so a finished batch leaves no core busy.
        sessionOptions.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        sessionOptions.EnableMemoryPattern = options.CpuMemoryPattern;
        sessionOptions.EnableCpuMemArena = options.CpuMemoryArena;
        if (useDirectML)
        {
            // DirectML needs memory patterns off and sequential execution.
            sessionOptions.EnableMemoryPattern = false;
            sessionOptions.AppendExecutionProvider_DML(options.DirectMLDeviceId);
        }

        var session = new InferenceSession(bytes, sessionOptions);
        try
        {
            return new OnnxEmbeddingModel(located, tokenizer, session, useDirectML ? LoadedExecutionProvider.DirectML : LoadedExecutionProvider.Cpu, file.Path, options.MaxAttentionCells);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Embeds texts.</summary>
    /// <param name="texts">The texts.</param>
    /// <param name="cancellationToken">A token that cancels between runs and terminates a run in progress.</param>
    /// <returns>One pooled (and, as the model card says, unit-length) vector per text, in order, and the tokens read.</returns>
    public (float[][] Vectors, int Tokens) Embed(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var encoded = new int[texts.Count][];
        var tokens = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            encoded[i] = Tokenizer.Encode(texts[i]);
            tokens += encoded[i].Length;
        }

        // Longest first, so that each run pads to a similar length; the results go back in input order.
        var order = Enumerable.Range(0, texts.Count).OrderByDescending(i => encoded[i].Length).ToArray();
        var vectors = new float[texts.Count][];

        _gate.Wait(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var start = 0;
            while (start < order.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = encoded[order[start]].Length;
                var count = (int)Math.Clamp(_maxAttentionCells / ((long)length * length), 1, order.Length - start);
                Run(encoded, order.AsSpan(start, count), length, vectors, cancellationToken);
                start += count;
            }
        }
        finally
        {
            _gate.Release();
        }

        return (vectors, tokens);
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static float[] Normalized(float[] vector)
    {
        double sum = 0;
        foreach (var x in vector)
        {
            sum += (double)x * x;
        }

        if (sum > 0)
        {
            var scale = (float)(1 / Math.Sqrt(sum));
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] *= scale;
            }
        }

        return vector;
    }

    private void Run(int[][] encoded, ReadOnlySpan<int> rows, int length, float[][] vectors, CancellationToken cancellationToken)
    {
        var n = rows.Length;
        var ids = new long[n * length];
        var mask = new long[n * length];
        for (var r = 0; r < n; r++)
        {
            var row = encoded[rows[r]];
            var offset = r * length;
            for (var j = 0; j < length; j++)
            {
                if (j < row.Length)
                {
                    ids[offset + j] = row[j];
                    mask[offset + j] = 1;
                }
                else
                {
                    ids[offset + j] = Tokenizer.PadId;
                }
            }
        }

        long[] shape = [n, length];
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var typesValue = _hasTokenTypes ? OrtValue.CreateTensorValueFromMemory(new long[n * length], shape) : null;
        var names = new List<string>(3) { "input_ids", "attention_mask" };
        var values = new List<OrtValue>(3) { idsValue, maskValue };
        if (typesValue is not null)
        {
            names.Add("token_type_ids");
            values.Add(typesValue);
        }

        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(() => runOptions.Terminate = true);
        using var outputs = _session.Run(runOptions, names, values, _outputNames);
        var output = outputs[0];
        var info = output.GetTensorTypeAndShape();
        var dims = info.Shape;
        if (dims.Length != 3 || dims[0] != n || dims[1] != length)
        {
            throw new InvalidOperationException($"The ONNX graph of {Model.Id} returned a tensor of shape [{string.Join(", ", dims)}]; [batch, tokens, hidden] was expected.");
        }

        var hidden = (int)dims[2];
        // The FP16 graph (DirectML) returns half-precision values; they are widened once. The float graph's output is read in place.
        ReadOnlySpan<float> data = info.ElementDataType == TensorElementType.Float16
            ? ToFloat(output.GetTensorDataAsSpan<Float16>())
            : output.GetTensorDataAsSpan<float>();

        for (var r = 0; r < n; r++)
        {
            var vector = new float[hidden];
            var rowStart = r * length * hidden;
            if (Model.Pooling == EmbeddingPooling.Cls)
            {
                data.Slice(rowStart, hidden).CopyTo(vector);
            }
            else
            {
                var real = encoded[rows[r]].Length;
                for (var t = 0; t < real; t++)
                {
                    var token = data.Slice(rowStart + (t * hidden), hidden);
                    for (var k = 0; k < hidden; k++)
                    {
                        vector[k] += token[k];
                    }
                }

                for (var k = 0; k < hidden; k++)
                {
                    vector[k] /= real;
                }
            }

            vectors[rows[r]] = Model.Normalize ? Normalized(vector) : vector;
        }
    }

    private static float[] ToFloat(ReadOnlySpan<Float16> values)
    {
        var result = new float[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = (float)values[i];
        }

        return result;
    }
}
