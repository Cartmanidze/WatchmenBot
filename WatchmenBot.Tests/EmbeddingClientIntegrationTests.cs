using Microsoft.Extensions.Configuration;
using WatchmenBot.Tests.Fixtures;
using Xunit;

namespace WatchmenBot.Tests;

[Collection("Integration")]
public class EmbeddingClientIntegrationTests
{
    private readonly TestConfiguration _config;
    private readonly int _expectedDimensions;

    public EmbeddingClientIntegrationTests(TestConfiguration config)
    {
        _config = config;
        // Read expected dimensions from config (1024 for Jina, 1536 for OpenAI)
        _expectedDimensions = config.Configuration.GetValue("Embeddings:Dimensions", 1024);
    }

    [Fact]
    public async Task GetEmbedding_Works_WhenApiKeyProvided()
    {
        if (!_config.HasOpenAiKey)
            return; // Skip if no API key

        var client = _config.CreateEmbeddingClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var embedding = await client.GetEmbeddingAsync("Привет, это тест embeddings!", ct: cts.Token);

        Assert.NotNull(embedding);
        Assert.Equal(_expectedDimensions, embedding.Length);
        Assert.Contains(embedding, x => x != 0);
    }

    [Fact]
    public async Task GetEmbeddings_Batch_Works()
    {
        if (!_config.HasOpenAiKey)
            return;

        var client = _config.CreateEmbeddingClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var texts = new[]
        {
            "Первое сообщение о погоде",
            "Второе сообщение о работе",
            "Третье сообщение о еде"
        };

        var embeddings = await client.GetEmbeddingsAsync(texts, ct: cts.Token);

        Assert.Equal(3, embeddings.Count);
        Assert.All(embeddings, e => Assert.Equal(_expectedDimensions, e.Length));
    }

    [Fact]
    public async Task SimilarTexts_HaveHigherCosineSimilarity()
    {
        if (!_config.HasOpenAiKey)
            return;

        var client = _config.CreateEmbeddingClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var texts = new[]
        {
            "Сегодня хорошая погода, светит солнце",  // 0: о погоде
            "На улице тепло и солнечно",              // 1: о погоде (похоже на 0)
            "Нужно купить молоко и хлеб"              // 2: о покупках (не похоже)
        };

        var embeddings = await client.GetEmbeddingsAsync(texts, ct: cts.Token);

        var sim01 = CosineSimilarity(embeddings[0], embeddings[1]); // погода-погода
        var sim02 = CosineSimilarity(embeddings[0], embeddings[2]); // погода-покупки

        // Похожие тексты должны иметь большее сходство
        Assert.True(sim01 > sim02, $"Expected weather texts to be more similar. sim01={sim01:F4}, sim02={sim02:F4}");
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
