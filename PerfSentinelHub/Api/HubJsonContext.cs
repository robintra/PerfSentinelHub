using System.Text.Json.Serialization;

namespace PerfSentinelHub.Api;

[JsonSerializable(typeof(StatusResponse))]
internal partial class HubJsonContext : JsonSerializerContext;
