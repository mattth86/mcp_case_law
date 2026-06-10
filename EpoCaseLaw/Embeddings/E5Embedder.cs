using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace EpoCaseLaw.Embeddings;

public sealed class E5Embedder : IDisposable
{
    // XLM-R / HF token ids: <s>=0, <pad>=1, </s>=2, <unk>=3.
    // SentencePiece piece ids are offset by +1 in the HF vocab (fairseq convention),
    // with spm's own specials (<unk>=0, <s>=1, </s>=2) replaced by the HF ids above.
    private const long BosId = 0;
    private const long PadId = 1;
    private const long EosId = 2;
    private const long UnkId = 3;
    private const int MaxTokens = 512;          // including <s> and </s>
    private const int MaxInnerTokens = MaxTokens - 2;

    private readonly InferenceSession _session;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly bool _wantsTokenTypeIds;

    public E5Embedder(string modelsDir)
    {
        var modelPath = Path.Combine(modelsDir, "model.onnx");
        var spmPath = Path.Combine(modelsDir, "sentencepiece.bpe.model");

        var options = new SessionOptions();
        options.AppendExecutionProvider_CPU();
        options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        _session = new InferenceSession(modelPath, options);
        _wantsTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");

        using var spmStream = File.OpenRead(spmPath);
        _tokenizer = SentencePieceTokenizer.Create(spmStream, addBeginningOfSentence: false, addEndOfSentence: false);
    }

    public float[] EmbedQuery(string text) => EmbedBatch([$"query: {text}"])[0];

    public float[] EmbedPassage(string text) => EmbedBatch([$"passage: {text}"])[0];

    public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts) =>
        EmbedBatch(texts.Select(t => $"passage: {t}").ToList());

    private long[] Tokenize(string text)
    {
        var spmIds = _tokenizer.EncodeToIds(text, false, false);
        var ids = new List<long>(Math.Min(spmIds.Count, MaxInnerTokens) + 2) { BosId };
        foreach (var spmId in spmIds)
        {
            if (ids.Count > MaxInnerTokens)
                break;
            // spm <unk>(0) → HF <unk>(3); all regular pieces shift by +1.
            ids.Add(spmId == 0 ? UnkId : spmId + 1);
        }

        ids.Add(EosId);
        return [.. ids];
    }

    private List<float[]> EmbedBatch(IReadOnlyList<string> texts)
    {
        var tokenized = texts.Select(Tokenize).ToArray();
        var batch = tokenized.Length;
        var seqLen = tokenized.Max(t => t.Length);

        var inputIds = new DenseTensor<long>([batch, seqLen]);
        var attentionMask = new DenseTensor<long>([batch, seqLen]);
        for (var b = 0; b < batch; b++)
        {
            for (var t = 0; t < seqLen; t++)
            {
                inputIds[b, t] = t < tokenized[b].Length ? tokenized[b][t] : PadId;
                attentionMask[b, t] = t < tokenized[b].Length ? 1 : 0;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        };
        if (_wantsTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>([batch, seqLen])));

        using var outputs = _session.Run(inputs);
        var hidden = outputs.First().AsTensor<float>();
        var hiddenSize = hidden.Dimensions[^1];

        var result = new List<float[]>(batch);
        for (var b = 0; b < batch; b++)
        {
            var pooled = new float[hiddenSize];
            var tokenCount = tokenized[b].Length;
            for (var t = 0; t < tokenCount; t++)
            {
                for (var h = 0; h < hiddenSize; h++)
                    pooled[h] += hidden[b, t, h];
            }

            for (var h = 0; h < hiddenSize; h++)
                pooled[h] /= tokenCount;

            var norm = TensorPrimitives.Norm(pooled);
            if (norm > 0)
                TensorPrimitives.Divide(pooled, norm, pooled);
            result.Add(pooled);
        }

        return result;
    }

    public static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBlob(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    public void Dispose() => _session.Dispose();
}
