using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GccPhat.RealTime.Analysis;

/// <summary>
/// YAMNet sound event classifier via ONNX Runtime.
///
/// Setup (one time):
///   pip install tensorflow tensorflow-hub tf2onnx
///   python -m tf2onnx.convert --tfhub https://tfhub.dev/google/yamnet/1 --output yamnet.onnx --opset 13
///   # also download the class map:
///   curl -o yamnet_class_map.csv https://raw.githubusercontent.com/tensorflow/models/master/research/audioset/yamnet/yamnet_class_map.csv
///   # place both files in: &lt;exe folder&gt;\Assets\
/// </summary>
public sealed class YamNetClassifier : IDisposable
{
    private const string ModelFile = "Assets\\yamnet.onnx";
    private const string ClassMapFile = "Assets\\yamnet_class_map.csv";
    private const int NumClasses = 521;

    private InferenceSession? _session;
    private string _inputName = string.Empty;
    private string _outputName = string.Empty;
    private bool _input2D; // true if model expects [1, N], false if [N]
    private string[] _classNames = Array.Empty<string>();

    public bool IsAvailable => _session is not null;
    public string StatusText { get; private set; } = "Not initialized.";

    public void Load()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string modelPath = Path.Combine(baseDir, ModelFile);
        if (!File.Exists(modelPath))
        {
            StatusText =
                $"Model not found: {modelPath}\n\n" +
                "To enable classification, run once:\n" +
                "  pip install tensorflow tensorflow-hub tf2onnx\n" +
                "  python -m tf2onnx.convert --tfhub https://tfhub.dev/google/yamnet/1 --output yamnet.onnx --opset 13\n" +
                "  curl -o yamnet_class_map.csv https://raw.githubusercontent.com/tensorflow/models/master/research/audioset/yamnet/yamnet_class_map.csv\n\n" +
                $"Then place both files in:\n  {Path.Combine(baseDir, "Assets\\")}";
            return;
        }

        try
        {
            var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
            _session = new InferenceSession(modelPath, options);

            // Detect input tensor name and shape.
            var inputMeta = _session.InputMetadata;
            _inputName = inputMeta.Keys.First();
            _input2D = inputMeta[_inputName].Dimensions.Length == 2;

            // Use the first output (scores).
            _outputName = _session.OutputMetadata.Keys.First();

            _classNames = LoadClassNames(Path.Combine(baseDir, ClassMapFile));
            StatusText = $"YAMNet ready — {_classNames.Length} classes, input: {(_input2D ? "2D" : "1D")}.";
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            StatusText = $"Failed to load model: {ex.Message}";
        }
    }

    /// <summary>
    /// Classify 16 kHz mono audio and return the top-N results sorted by score descending.
    /// </summary>
    public ClassificationResult[] Classify(float[] audio16kHz, int topN = 10)
    {
        if (_session is null) return Array.Empty<ClassificationResult>();

        // Build input tensor: [N] or [1, N] depending on model.
        DenseTensor<float> tensor;
        if (_input2D)
            tensor = new DenseTensor<float>(audio16kHz, new[] { 1, audio16kHz.Length });
        else
            tensor = new DenseTensor<float>(audio16kHz, new[] { audio16kHz.Length });

        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(inputs);

        // Scores shape: [n_patches, 521] or [521]. Average over patches.
        float[] scores = AveragePatchScores(outputs.First());
        return TopN(scores, topN);
    }

    private float[] AveragePatchScores(DisposableNamedOnnxValue output)
    {
        var tensor = output.AsTensor<float>();
        int rank = tensor.Dimensions.Length;

        if (rank == 1)
            return tensor.ToArray();

        // [n_patches, 521] → average
        int patches = tensor.Dimensions[0];
        int classes = tensor.Dimensions[1];
        var avg = new float[classes];
        for (int p = 0; p < patches; p++)
            for (int c = 0; c < classes; c++)
                avg[c] += tensor[p, c];
        for (int c = 0; c < classes; c++) avg[c] /= patches;
        return avg;
    }

    private ClassificationResult[] TopN(float[] scores, int n)
    {
        int len = Math.Min(scores.Length, NumClasses);
        var ranked = new List<(float score, int idx)>(len);
        for (int i = 0; i < len; i++) ranked.Add((scores[i], i));
        ranked.Sort((a, b) => b.score.CompareTo(a.score));
        int take = Math.Min(n, ranked.Count);
        var results = new ClassificationResult[take];
        for (int i = 0; i < take; i++)
        {
            string label = ranked[i].idx < _classNames.Length ? _classNames[ranked[i].idx] : $"Class {ranked[i].idx}";
            results[i] = new ClassificationResult(label, ranked[i].score);
        }
        return results;
    }

    private static string[] LoadClassNames(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            var fallback = new string[NumClasses];
            for (int i = 0; i < NumClasses; i++) fallback[i] = $"Class {i}";
            return fallback;
        }
        // CSV format: index,mid,display_name (first row is header)
        var names = new Dictionary<int, string>();
        foreach (string line in File.ReadLines(csvPath).Skip(1))
        {
            string[] parts = line.Split(',');
            if (parts.Length >= 3 && int.TryParse(parts[0].Trim(), out int idx))
                names[idx] = parts[2].Trim().Trim('"');
        }
        int maxIdx = names.Count > 0 ? names.Keys.Max() : NumClasses - 1;
        var result = new string[maxIdx + 1];
        for (int i = 0; i <= maxIdx; i++)
            result[i] = names.TryGetValue(i, out string? n) ? n : $"Class {i}";
        return result;
    }

    public void Dispose() => _session?.Dispose();
}
