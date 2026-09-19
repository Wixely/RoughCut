namespace RoughCut.Mcp;

public sealed record SpeechSettings(string? ModelPath, string Language, int ChunkSeconds);
public sealed record DiarizationSettings(string? SegmentationModelPath, string? EmbeddingModelPath,
    int SpeakerCount, float Threshold);
public sealed record QwenSettings(string? Endpoint, string? ApiKey, int TimeoutSeconds);
