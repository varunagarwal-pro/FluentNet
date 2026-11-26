# FluentNet — Quality & UI Constraints (`CONSTRAINTS.md`)

Last reviewed: 2026-11-27

## Floor (Always Enforced)
- Zero compiler errors and zero compiler warnings in `FluentNet.*` code.
- Zero unimplemented stubs, empty `catch {}` blocks that swallow failures, or suppressed checks.
- Strictly 64-bit (`x64` / `ARM64`); `AnyCPU` / `x86` prohibited.
- Both UI heads (`FluentNet.App` and `FluentNet.Classic`) launch `asInvoker`; only `FluentNet.Cli` handles elevation.
- `evidence_gate.py scan --git-changed` must report `0 ERROR, 0 WARN, 0 suppressed`.

## Enforced With Automated Tests & Gates

| Dimension | Rule | Checked By | Runs At |
|---|---|---|---|
| **Build & Tests** | 100% passing unit + UI layout regression tests | `dotnet test src/FluentNet.Tests/FluentNet.Tests.csproj -c Release` | every task |
| **Dual-Head Parity** | `.NET 4.8` WPF (`FluentNet.Classic`) & `.NET 10` WinUI 3 (`FluentNet.App`) both compile cleanly | `tools/Publish-Local.ps1` | every change |
| **Scrollbar Safety** | No `ScrollViewer` vertical scrollbar may overlap buttons/badges (`Padding`/`Margin` right `>= 18`, header toolbar fixed outside `ScrollViewer`) | `UiAndEngineRegressionTests.ScrollViewers_DoNotOverlapContent` | every test run |
| **Dialog Width & Visibility** | Multi-column `ContentDialog` must override `ContentDialogMaxWidth >= 820` so `Current → Proposed`, `Default`, and `Target` never clip | `UiAndEngineRegressionTests.DiffDialog_OverridesContentDialogMaxWidth` | every test run |
| **Icon Craftsmanship** | App icon (`AppIcon.ico` / `fluentnet-icon.png`) uses the user-approved dark-navy rounded squircle wave silhouette on a transparent RGBA canvas | `UiAndEngineRegressionTests.AppIcon_HasTransparentBackground` | every test run |
| **Clean Adapter Discovery** | Zero NDIS/WFP/Filter Driver/WAN Miniport noise in adapter lists | `UiAndEngineRegressionTests.NoiseFilter_ExcludesFilterDrivers` | every test run |
| **Static Scanner** | Zero stubs, zero placeholders, zero suppressed findings | `python D:\SentinelLabs\tools\evidence_gate.py scan --git-changed` | every task |
