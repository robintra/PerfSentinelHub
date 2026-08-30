using System.Text.Json.Serialization;

namespace PerfSentinelHub.Api;

// Pinned on the context so a payload written through an explicit JsonTypeInfo
// gets the same casing as one written through the ASP.NET options.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(DetectionKnob))]
[JsonSerializable(typeof(ImportResponse))]
[JsonSerializable(typeof(IReadOnlyList<SourceResponse>))]
[JsonSerializable(typeof(SubmittedAnalysis))]
[JsonSerializable(typeof(AnalysisProblem))]
internal partial class HubJsonContext : JsonSerializerContext;
