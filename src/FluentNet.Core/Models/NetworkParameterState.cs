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
    UnsupportedOnAdapter,
    ReadOnlyOnOs
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
    bool RequiresReboot,
    string? Description = null);

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

public sealed record ApplyItemResult(
    string ParameterId,
    bool Succeeded,
    string Message);

public sealed record ApplyReport(
    bool AllSucceeded,
    IReadOnlyList<ApplyItemResult> Results,
    string? SnapshotPath = null);
