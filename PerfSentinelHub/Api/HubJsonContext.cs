using System.Text.Json.Serialization;

namespace PerfSentinelHub.Api;

[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(ImportResponse))]
[JsonSerializable(typeof(IReadOnlyList<SourceResponse>))]
internal partial class HubJsonContext : JsonSerializerContext;
