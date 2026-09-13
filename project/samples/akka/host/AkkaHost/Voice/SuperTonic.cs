// ---------------------------------------------------------------------------
// SuperTonic-3 ONNX text-to-speech.
//
// Ported from Supertone Inc's official C# reference (csharp/Helper.cs, MIT,
// Copyright (c) Supertone Inc) by way of AgentZeroLite's
// Project/ZeroCommon/Voice/SuperTonicEngine.cs. Trimmed to what this host needs:
// synthesis to float PCM, no WAV writer, no downloader - the model is expected to
// be installed already.
//
// The property that makes this usable here: SuperTonic needs no espeak-ng, no g2p,
// no Python. "Tokenization" is a Unicode-codepoint lookup through
// unicode_indexer.json, and the runtime is four ONNX graphs. That also makes it the
// only TTS on this machine with a chance of surviving Native AOT - Windows
// System.Speech is COM-based and does not.
// ---------------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AkkaHost.Voice;

/// <summary>Path convention for the installed SuperTonic-3 bundle.</summary>
public static class SuperTonicModel
{
    public static readonly string[] OnnxFiles =
    [
        "text_encoder.onnx", "duration_predictor.onnx", "vector_estimator.onnx", "vocoder.onnx",
    ];

    public static readonly string[] DataFiles = ["tts.json", "unicode_indexer.json"];

    /// <summary>
    /// Where AgentZeroLite installs the bundle. This host never downloads anything;
    /// it uses what is already on disk (383 MB) and says so plainly when it is absent.
    /// </summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "models", "supertonic");

    public static string VoiceStylePath(string modelDir, string voice)
        => Path.Combine(modelDir, "voice_styles", $"{voice}.json");

    public static bool IsPresent(string modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir)) return false;
        foreach (var f in OnnxFiles) if (!File.Exists(Path.Combine(modelDir, f))) return false;
        foreach (var f in DataFiles) if (!File.Exists(Path.Combine(modelDir, f))) return false;
        return true;
    }
}

public static class SuperTonicLanguages
{
    public static readonly string[] Available =
    [
        "en", "ko", "ja", "ar", "bg", "cs", "da", "de", "el", "es", "et", "fi",
        "fr", "hi", "hr", "hu", "id", "it", "lt", "lv", "nl", "pl", "pt", "ro",
        "ru", "sk", "sl", "sv", "tr", "uk", "vi", "na",
    ];

    /// <summary>Unknown tags become "na" (script auto-detect) instead of throwing.</summary>
    public static string Coerce(string? lang)
        => !string.IsNullOrWhiteSpace(lang) && Available.Contains(lang) ? lang! : "na";
}

/// <summary>tts.json: sample rate + latent geometry.</summary>
public sealed class SuperTonicConfig
{
    public int SampleRate { get; init; }
    public int BaseChunkSize { get; init; }
    public int ChunkCompressFactor { get; init; }
    public int LatentDim { get; init; }

    public static SuperTonicConfig Load(string modelDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "tts.json")));
        var ae = doc.RootElement.GetProperty("ae");
        var ttl = doc.RootElement.GetProperty("ttl");
        return new SuperTonicConfig
        {
            SampleRate = ae.GetProperty("sample_rate").GetInt32(),
            BaseChunkSize = ae.GetProperty("base_chunk_size").GetInt32(),
            ChunkCompressFactor = ttl.GetProperty("chunk_compress_factor").GetInt32(),
            LatentDim = ttl.GetProperty("latent_dim").GetInt32(),
        };
    }
}

/// <summary>One speaker style: two flattened tensors plus their shapes.</summary>
public sealed class SuperTonicStyle(float[] ttl, long[] ttlShape, float[] dp, long[] dpShape)
{
    public float[] Ttl { get; } = ttl;
    public long[] TtlShape { get; } = ttlShape;
    public float[] Dp { get; } = dp;
    public long[] DpShape { get; } = dpShape;

    public static SuperTonicStyle Load(string voiceStylePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(voiceStylePath));
        var root = doc.RootElement;

        var ttlDims = Int64Array(root.GetProperty("style_ttl").GetProperty("dims"));
        var dpDims = Int64Array(root.GetProperty("style_dp").GetProperty("dims"));
        var ttl = Flatten3D(root.GetProperty("style_ttl").GetProperty("data"));
        var dp = Flatten3D(root.GetProperty("style_dp").GetProperty("data"));

        return new SuperTonicStyle(ttl, [1, ttlDims[1], ttlDims[2]], dp, [1, dpDims[1], dpDims[2]]);
    }

    private static float[] Flatten3D(JsonElement element)
    {
        var flat = new List<float>();
        foreach (var batch in element.EnumerateArray())
            foreach (var row in batch.EnumerateArray())
                foreach (var value in row.EnumerateArray())
                    flat.Add(value.GetSingle());
        return flat.ToArray();
    }

    private static long[] Int64Array(JsonElement element)
    {
        var result = new List<long>();
        foreach (var value in element.EnumerateArray()) result.Add(value.GetInt64());
        return result.ToArray();
    }
}

/// <summary>Text normalization + chunking, exactly as the reference applies it.</summary>
public static class SuperTonicText
{
    private static readonly (string from, string to)[] SymbolReplacements =
    [
        ("–", "-"), ("‑", "-"), ("—", "-"), ("_", " "),
        ("“", "\""), ("”", "\""), ("‘", "'"), ("’", "'"),
        ("´", "'"), ("`", "'"),
        ("[", " "), ("]", " "), ("|", " "), ("/", " "), ("#", " "),
        ("→", " "), ("←", " "),
    ];

    private static readonly (string from, string to)[] ExprReplacements =
    [
        ("@", " at "), ("e.g.,", "for example, "), ("i.e.,", "that is, "),
    ];

    public static string RemoveEmojis(string text)
    {
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            int codePoint;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                codePoint = text[i];
            }

            var isEmoji =
                (codePoint >= 0x1F600 && codePoint <= 0x1F64F) ||
                (codePoint >= 0x1F300 && codePoint <= 0x1F5FF) ||
                (codePoint >= 0x1F680 && codePoint <= 0x1F6FF) ||
                (codePoint >= 0x1F700 && codePoint <= 0x1F77F) ||
                (codePoint >= 0x1F780 && codePoint <= 0x1F7FF) ||
                (codePoint >= 0x1F800 && codePoint <= 0x1F8FF) ||
                (codePoint >= 0x1F900 && codePoint <= 0x1F9FF) ||
                (codePoint >= 0x1FA00 && codePoint <= 0x1FA6F) ||
                (codePoint >= 0x1FA70 && codePoint <= 0x1FAFF) ||
                (codePoint >= 0x2600 && codePoint <= 0x26FF) ||
                (codePoint >= 0x2700 && codePoint <= 0x27BF) ||
                (codePoint >= 0x1F1E6 && codePoint <= 0x1F1FF);

            if (!isEmoji) result.Append(codePoint > 0xFFFF ? char.ConvertFromUtf32(codePoint) : (char)codePoint);
        }
        return result.ToString();
    }

    /// <summary>
    /// Hangul syllable -> initial/medial/final jamo, by the Unicode algorithm.
    ///
    /// SuperTonic's unicode_indexer.json has no composed Hangul at all: U+AC00 and every
    /// other syllable map to -1, while the jamo at U+1100/U+1161/U+11A8 have real ids. The
    /// reference implementation gets the decomposition from NFKD - but that depends on the
    /// runtime's globalization data, and with InvariantGlobalization on it silently does
    /// nothing, which turned every Korean answer into noise. Doing it explicitly costs
    /// fifteen lines and cannot be switched off by a build property.
    /// </summary>
    public static string DecomposeHangul(string text)
    {
        const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
        const int VCount = 21, TCount = 28, NCount = VCount * TCount;   // 588
        const int SCount = 19 * NCount;                                  // 11172

        var result = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            var index = c - SBase;
            if (index < 0 || index >= SCount)
            {
                result.Append(c);
                continue;
            }
            result.Append((char)(LBase + index / NCount));
            result.Append((char)(VBase + index % NCount / TCount));
            var trailing = index % TCount;
            if (trailing != 0) result.Append((char)(TBase + trailing));
        }
        return result.ToString();
    }

    public static string Preprocess(string text, string lang)
    {
        lang = SuperTonicLanguages.Coerce(lang);

        text = text.Normalize(NormalizationForm.FormKD);
        text = DecomposeHangul(text);   // NFKD may or may not have done it; this always does
        text = RemoveEmojis(text);

        foreach (var (from, to) in SymbolReplacements) text = text.Replace(from, to);
        text = Regex.Replace(text, @"[♥☆♡©\\]", "");
        foreach (var (from, to) in ExprReplacements) text = text.Replace(from, to);

        text = Regex.Replace(text, @" ,", ",");
        text = Regex.Replace(text, @" \.", ".");
        text = Regex.Replace(text, @" !", "!");
        text = Regex.Replace(text, @" \?", "?");
        text = Regex.Replace(text, @" ;", ";");
        text = Regex.Replace(text, @" :", ":");
        text = Regex.Replace(text, @" '", "'");

        while (text.Contains("\"\"")) text = text.Replace("\"\"", "\"");
        while (text.Contains("''")) text = text.Replace("''", "'");
        while (text.Contains("``")) text = text.Replace("``", "`");

        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (!Regex.IsMatch(text, "[.!?;:,'\"“”‘’)\\]}…。」』】〉》›»]$")) text += ".";

        return $"<{lang}>{text}</{lang}>";
    }

    /// <summary>Sentence-boundary chunking; ko/ja cap at 120 chars, others 300.</summary>
    public static List<string> Chunk(string text, string lang)
    {
        var maxLen = lang is "ko" or "ja" ? 120 : 300;
        var chunks = new List<string>();

        var paragraphs = Regex.Split(text.Trim(), @"\n\s*\n+")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var sentenceRegex = new Regex(
            @"(?<!Mr\.|Mrs\.|Ms\.|Dr\.|Prof\.|Sr\.|Jr\.|Ph\.D\.|etc\.|e\.g\.|i\.e\.|vs\.|Inc\.|Ltd\.|Co\.|Corp\.|St\.|Ave\.|Blvd\.)(?<!\b[A-Z]\.)(?<=[.!?])\s+");

        foreach (var paragraph in paragraphs)
        {
            var current = "";
            foreach (var sentence in sentenceRegex.Split(paragraph))
            {
                if (sentence.Length == 0) continue;
                if (current.Length + sentence.Length + 1 <= maxLen)
                {
                    if (current.Length > 0) current += " ";
                    current += sentence;
                }
                else
                {
                    if (current.Length > 0) chunks.Add(current.Trim());
                    current = sentence;
                }
            }
            if (current.Length > 0) chunks.Add(current.Trim());
        }

        if (chunks.Count == 0) chunks.Add(text.Trim());
        return chunks;
    }
}

/// <summary>Code point -> token id through unicode_indexer.json. Unknown points map to 0.</summary>
public sealed class SuperTonicTokenizer
{
    private readonly long[] _indexer;

    private SuperTonicTokenizer(long[] indexer) => _indexer = indexer;

    public static SuperTonicTokenizer Load(string modelDir)
    {
        // JsonDocument, not JsonSerializer<long[]>: the latter needs reflection or a
        // source generator, which Native AOT trims.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "unicode_indexer.json")));
        var list = new List<long>(doc.RootElement.GetArrayLength());
        foreach (var value in doc.RootElement.EnumerateArray()) list.Add(value.GetInt64());
        return new SuperTonicTokenizer(list.ToArray());
    }

    public long[] Encode(string preprocessed)
    {
        var ids = new long[preprocessed.Length];
        for (var i = 0; i < preprocessed.Length; i++)
        {
            int cp = preprocessed[i];
            if (cp >= 0 && cp < _indexer.Length) ids[i] = _indexer[cp];
        }
        return ids;
    }
}

/// <summary>
/// The four ONNX sessions and the text_encoder -> duration_predictor ->
/// vector_estimator (flow matching) -> vocoder pipeline.
/// </summary>
public sealed class SuperTonicSynthesizer : IDisposable
{
    private readonly SuperTonicConfig _cfg;
    private readonly SuperTonicTokenizer _tokenizer;
    private readonly InferenceSession _dp;
    private readonly InferenceSession _textEnc;
    private readonly InferenceSession _vectorEst;
    private readonly InferenceSession _vocoder;

    public int SampleRate => _cfg.SampleRate;

    private SuperTonicSynthesizer(SuperTonicConfig cfg, SuperTonicTokenizer tokenizer, InferenceSession dp,
        InferenceSession textEnc, InferenceSession vectorEst, InferenceSession vocoder)
    {
        _cfg = cfg;
        _tokenizer = tokenizer;
        _dp = dp;
        _textEnc = textEnc;
        _vectorEst = vectorEst;
        _vocoder = vocoder;
    }

    public static SuperTonicSynthesizer Load(string modelDir)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 0,  // 0 = one thread per core
        };
        return new SuperTonicSynthesizer(
            SuperTonicConfig.Load(modelDir),
            SuperTonicTokenizer.Load(modelDir),
            new InferenceSession(Path.Combine(modelDir, "duration_predictor.onnx"), opts),
            new InferenceSession(Path.Combine(modelDir, "text_encoder.onnx"), opts),
            new InferenceSession(Path.Combine(modelDir, "vector_estimator.onnx"), opts),
            new InferenceSession(Path.Combine(modelDir, "vocoder.onnx"), opts));
    }

    /// <summary>Float PCM in [-1,1] at <see cref="SampleRate"/>, chunked for long text.</summary>
    public float[] Synthesize(string text, string lang, SuperTonicStyle style, int totalStep,
        float speed = 1.05f, float silenceSeconds = 0.3f)
    {
        lang = SuperTonicLanguages.Coerce(lang);
        var wav = new List<float>();
        foreach (var chunk in SuperTonicText.Chunk(text, lang))
        {
            var part = InferOne(chunk, lang, style, totalStep, speed);
            if (wav.Count > 0) wav.AddRange(new float[(int)(silenceSeconds * SampleRate)]);
            wav.AddRange(part);
        }
        return wav.ToArray();
    }

    private float[] InferOne(string chunk, string lang, SuperTonicStyle style, int totalStep, float speed)
    {
        var preprocessed = SuperTonicText.Preprocess(chunk, lang);
        var textIds = _tokenizer.Encode(preprocessed);

        var textIdsTensor = new DenseTensor<long>(textIds, [1, textIds.Length]);
        var textMaskTensor = new DenseTensor<float>(Mask(preprocessed.Length, textIds.Length),
            [1, 1, textIds.Length]);
        var styleTtl = new DenseTensor<float>(style.Ttl, style.TtlShape.Select(x => (int)x).ToArray());
        var styleDp = new DenseTensor<float>(style.Dp, style.DpShape.Select(x => (int)x).ToArray());

        // 1) durations
        using var dpOut = _dp.Run(
        [
            NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
            NamedOnnxValue.CreateFromTensor("style_dp", styleDp),
            NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor),
        ]);
        var duration = dpOut.First(o => o.Name == "duration").AsTensor<float>().ToArray();
        for (var i = 0; i < duration.Length; i++) duration[i] /= speed;

        // 2) text embedding. Copied out so it outlives teOut.
        using var teOut = _textEnc.Run(
        [
            NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
            NamedOnnxValue.CreateFromTensor("style_ttl", styleTtl),
            NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor),
        ]);
        var textEmb = teOut.First(o => o.Name == "text_emb").AsTensor<float>();
        var textEmbTensor = new DenseTensor<float>(textEmb.ToArray(), textEmb.Dimensions.ToArray());

        // 3) noisy latent
        var wavLenMax = duration.Max() * SampleRate;
        var wavLen = (long)(duration[0] * SampleRate);
        var chunkSize = _cfg.BaseChunkSize * _cfg.ChunkCompressFactor;
        var latentLen = (int)((wavLenMax + chunkSize - 1) / chunkSize);
        var latentDim = _cfg.LatentDim * _cfg.ChunkCompressFactor;
        var latentMask = Mask((wavLen + chunkSize - 1) / chunkSize, latentLen);

        var rng = new Random();
        var xt = new float[latentDim * latentLen];
        for (var d = 0; d < latentDim; d++)
        {
            for (var t = 0; t < latentLen; t++)
            {
                // Box-Muller standard normal, masked to the valid latent length.
                var u1 = 1.0 - rng.NextDouble();
                var u2 = 1.0 - rng.NextDouble();
                var z = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                xt[d * latentLen + t] = z * latentMask[t];
            }
        }
        int[] latentDims = [1, latentDim, latentLen];
        int[] latentMaskDims = [1, 1, latentLen];

        // 4) flow-matching denoise
        for (var step = 0; step < totalStep; step++)
        {
            using var veOut = _vectorEst.Run(
            [
                NamedOnnxValue.CreateFromTensor("noisy_latent", new DenseTensor<float>(xt, latentDims)),
                NamedOnnxValue.CreateFromTensor("text_emb", textEmbTensor),
                NamedOnnxValue.CreateFromTensor("style_ttl", styleTtl),
                NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor),
                NamedOnnxValue.CreateFromTensor("latent_mask", new DenseTensor<float>(latentMask, latentMaskDims)),
                NamedOnnxValue.CreateFromTensor("total_step",
                    new DenseTensor<float>(new[] { (float)totalStep }, new[] { 1 })),
                NamedOnnxValue.CreateFromTensor("current_step",
                    new DenseTensor<float>(new[] { (float)step }, new[] { 1 })),
            ]);
            var denoised = veOut.First(o => o.Name == "denoised_latent").AsTensor<float>();
            for (var i = 0; i < xt.Length; i++) xt[i] = denoised.GetValue(i);
        }

        // 5) vocoder
        using var vocOut = _vocoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("latent", new DenseTensor<float>(xt, latentDims)),
        ]);
        return vocOut.First(o => o.Name == "wav_tts").AsTensor<float>().ToArray();
    }

    private static float[] Mask(long length, int maxLen)
    {
        var mask = new float[maxLen];
        for (var i = 0; i < maxLen; i++) mask[i] = i < length ? 1.0f : 0.0f;
        return mask;
    }

    public void Dispose()
    {
        _dp.Dispose();
        _textEnc.Dispose();
        _vectorEst.Dispose();
        _vocoder.Dispose();
    }
}
