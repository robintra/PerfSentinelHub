using System.Text.Json.Serialization;

namespace PerfSentinelHub.Api;

[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(ImportResponse))]
internal partial class HubJsonContext : JsonSerializerContext;
