using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cai.Reference.Scoring;

/// <summary>The evidence bundle: the handover point between measurement and scoring.</summary>
public sealed class EvidenceBundle
{
    [JsonPropertyName("rubricVersion")] public required string RubricVersion { get; set; }
    [JsonPropertyName("commit")] public string? Commit { get; set; }
    [JsonPropertyName("qualityBar")] public string? QualityBar { get; set; }
    [JsonPropertyName("analyzableProjects")] public int AnalyzableProjects { get; set; }
    [JsonPropertyName("productionLoc")] public int ProductionLoc { get; set; }
    [JsonPropertyName("dimensions")] public List<DimensionEvidence> Dimensions { get; set; } = new();
    [JsonPropertyName("metaDimensions")] public List<MetaDimensionEvidence> MetaDimensions { get; set; } = new();
    [JsonPropertyName("lenses")] public List<LensEvidence>? Lenses { get; set; }
    [JsonPropertyName("headlineScore")] public double? HeadlineScore { get; set; }
    /// <summary>Everything else the bundle carries (descriptive fields such as rebuildCost, busFactor, notMeasured). Never folded.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static EvidenceBundle Parse(string json) =>
        JsonSerializer.Deserialize<EvidenceBundle>(json, JsonOptions) ?? throw new InvalidOperationException("empty evidence");

    public static EvidenceBundle Load(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions) + "\n";
}

public sealed class DimensionEvidence
{
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("category")] public required string Category { get; set; }
    [JsonPropertyName("score")] public double Score { get; set; }
    /// <summary>Weight inside the category. Absent reads as 0, which makes the dimension absent (the oracle agrees).</summary>
    [JsonPropertyName("confidence")] public double? Confidence { get; set; }
    /// <summary>Share of the relevant surface measured; scales the score. Absent reads as 1.</summary>
    [JsonPropertyName("coverage")] public double? Coverage { get; set; }
    [JsonPropertyName("advisory")] public bool? Advisory { get; set; }
}

public sealed class MetaDimensionEvidence
{
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("lens")] public required string Lens { get; set; }
    [JsonPropertyName("score")] public double? Score { get; set; }
    [JsonPropertyName("advisory")] public bool? Advisory { get; set; }
}

public sealed class LensEvidence
{
    [JsonPropertyName("lens")] public required string Lens { get; set; }
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("owaWeight")] public double OwaWeight { get; set; }
}
