using System.Text.Json.Serialization;
using FluentNet.Core.Models;

namespace FluentNet.Core.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(NetworkParameterState))]
[JsonSerializable(typeof(ChangePlanItem))]
[JsonSerializable(typeof(SnapshotManifest))]
[JsonSerializable(typeof(ApplyItemResult))]
[JsonSerializable(typeof(ApplyReport))]
[JsonSerializable(typeof(CustomProfileDocument))]
[JsonSerializable(typeof(OsEnvironmentInfo))]
[JsonSerializable(typeof(AdapterAdvancedPropertyInfo))]
[JsonSerializable(typeof(NetworkAdapterInfo))]
[JsonSerializable(typeof(HostScanReport))]
[JsonSerializable(typeof(List<NetworkParameterState>))]
[JsonSerializable(typeof(List<ChangePlanItem>))]
[JsonSerializable(typeof(List<ApplyItemResult>))]
public sealed partial class FluentNetJsonContext : JsonSerializerContext
{
}
