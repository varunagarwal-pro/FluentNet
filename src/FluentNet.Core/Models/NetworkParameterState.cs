namespace FluentNet.Core.Models;

public enum ParameterSourceKind
{
    Cim,
    Netsh,
    Registry,
    DriverAdvancedProperty
}

public enum ParameterSupportStatus
{
    Supported,
    NotSet,
    UnsupportedOnOs,
    UnsupportedOnAdapter
}

public sealed record NetworkParameterState(
    string Id,
    string DisplayName,
    string Category,
    string CurrentValue,
    string WindowsDefaultValue,
    string ExactPath,
    ParameterSourceKind SourceKind,
    ParameterSupportStatus SupportStatus,
    bool RequiresReboot);

public sealed record ChangePlanItem(
    string ParameterId,
    string DisplayName,
    string OldValue,
    string NewValue,
    string WindowsDefaultValue,
    string ExactPath,
    ParameterSourceKind SourceKind,
    bool RequiresReboot);

public sealed record SnapshotManifest(
    string SnapshotId,
    DateTimeOffset CreatedAtUtc,
    string OsBuild,
    string ProfileName,
    IReadOnlyList<ChangePlanItem> Items);
