# 📋 FluentNet — Status Tracker

![Project](https://img.shields.io/badge/project-FluentNet-blue) ![Status](https://img.shields.io/badge/status-in%20progress-yellow) ![Version](https://img.shields.io/badge/version-0.1.0-lightgrey)

> [!NOTE]
> Master task ledger for **FluentNet** — the 64-bit, parallel dual-head rebuild of SG TCP Optimizer (`WinUI 3 .NET 10 NativeAOT` + `WPF .NET 4.8 Zero-Dep` + `Cli`).
> Grounded in [`architecture_plan.md`](temp/architecture_plan.md), [`windows_network_research.md`](temp/windows_network_research.md), [`ui_design_and_workflow.md`](temp/ui_design_and_workflow.md), and [`settings_audit.md`](temp/settings_audit.md).

**Legend:** ✅ done | ⬜ pending | 📝 note

---

## 🎨 1. Branding, Docs, Governance & CI/CD ![In Progress](https://img.shields.io/badge/-in%20progress-yellow)

📝 Approved by user on 2026-11-27 (GO). Tasks below are locked.

- ⬜ **Task 1 (Branding & Visual Assets):** Generate multi-size app icon (`.ico` / `.png` for WinUI 3, WPF, and Windows shell), README hero banner, and brand SVG/PNG assets under `assets/`.
- ⬜ **Task 2 (Architecture Documentation):** Create `docs/ARCHITECTURE.md` covering the dual-head solution, 64-bit parallel/async execution model, `asInvoker` + elevated `.Cli` privilege split, CIM/WMI + `iphlpapi.dll` + 64-bit Registry layers, and Host-Scan recommendation engine (incorporating `windows_network_research.md` and `ui_design_and_workflow.md`).
- ⬜ **Task 3 (Flagship README):** Create `README.md` with hero branding banner, shields.io badges, project What & Why, feature comparison vs. legacy SG TCP Optimizer, concise architecture summary linking to `docs/ARCHITECTURE.md`, safety/no-telemetry guarantees, and build instructions.
- ⬜ **Task 4 (PR, Issues, CodeRabbit & GitHub Actions CI/CD):** Create `.coderabbit.yaml` (`main`, `dev` auto-review per `Docs/CodeRabbit.md`), `.github/ISSUE_TEMPLATE/` (bug report & feature request), `.github/PULL_REQUEST_TEMPLATE.md`, and `.github/workflows/` (`ci.yml` building `x64`/`arm64` WinUI 3 + `x64` WPF `net48` + `xUnit`, `release.yml`, `codeql.yml`).

---

## 🏗️ 2. Dual-Head Solution & NativeAOT Skeleton ![Pending](https://img.shields.io/badge/-pending-red)

📝 Proposed: awaiting user approval.

- ⬜ **Task 5 (Solution & Project Scaffold):** Create `.slnx` and projects under `src/`: `<Name>.Core` (`net10.0-windows10.0.19041.0`), `<Name>.App` (WinUI 3 Unpackaged, `x64;arm64`), `<Name>.Classic` (WPF `net48`, `x64`, zero-NuGet, linked `.Core` `.cs` files), `<Name>.Cli` (`net10.0`), and `<Name>.Tests` (`xUnit`). Enforce 64-bit-only targets and UNIX LF line endings.
- ⬜ **Task 6 (NativeAOT & Manifest Configuration):** Configure reflection-free `System.Text.Json` source-gen in `.Core`, `PublishAot=true` + `WindowsAppSDK.AI`/`ML` asset exclusion in `.App` (matching `FluentCleaner.csproj`), and `asInvoker` + per-monitor DPI manifests in both UIs.

---

## 🔍 3. 64-Bit Parallel Host-Scan & Read-Only Discovery ![Pending](https://img.shields.io/badge/-pending-red)

📝 Proposed: awaiting user approval.

- ⬜ **Task 7 (Parallel Host & NIC Discovery):** Implement async/concurrent OS build/edition detection, CPU physical core topology probe, and NIC enumeration (adapter alias, GUID, media type Ethernet/Wi-Fi/VPN, link speed, IPv4/IPv6, MTU, PPPoE) in `.Core`.
- ⬜ **Task 8 (Concurrent Stack & Registry Readers):** Implement parallel readers in `.Core` for `ROOT\StandardCimv2` (`MSFT_NetTCPSetting`, `MSFT_NetOffloadGlobalSetting`, `MSFT_NetAdapterLso/Checksum/Rss/Rsc/Uso/AdvancedProperty`), `netsh`, and 64-bit `HKLM` Registry keys (`windows_network_research.md` §1), cleanly flagging unsupported OS/NIC items.
- ⬜ **Task 9 (Read-Only Dashboard, General & Advanced UIs):** Implement Dashboard (Host Scan summary), General Settings, and grouped Advanced Settings views in both WinUI 3 (`NavigationView` + `SettingsCard`/`SettingsExpander`) and WPF Classic (`TabControl`), adhering to `ui_design_and_workflow.md` accessibility & spacing contracts with zero UAC prompts.

---

## 🧮 4. BDP, Adaptive Profiles & Transparent Diff Modal ![Pending](https://img.shields.io/badge/-pending-red)

📝 Proposed: awaiting user approval.

- ⬜ **Task 10 (BDP / RWIN / MSS Calculator):** Implement BDP/RWIN/MSS calculation logic in `.Core` with `xUnit` coverage and bind it to the BDP section in both UIs.
- ⬜ **Task 11 (Host-Scan Adaptive Profile Engine):** Implement `Windows Default`, `Current`, `Recommended Optimal`, `Gaming / Low-Latency`, and `Custom` profile generators in `.Core` driven by the 6 host signals (`windows_network_research.md` §2), including the Win11 24H2/25H2 `BBR2` + `loopbacklargemtu=disable` safety guard.
- ⬜ **Task 12 (Transparent Diff Engine & Preview Modal):** Implement `DiffEngine` (`Value Name | Current | Proposed | Default | Exact Path | Reboot`) in `.Core` and wire the Diff Preview Modal in WinUI 3 (`ContentDialog`) and WPF (`DataGrid` window).

---

## 🛡️ 5. Snapshots, Script Export & Elevated Apply / Rollback ![Pending](https://img.shields.io/badge/-pending-red)

📝 Proposed: awaiting user approval.

- ⬜ **Task 13 (Snapshots & `.ps1`/`.reg` Exporter):** Implement timestamped JSON snapshot storage (`%LocalAppData%\<Name>\Snapshots\`), human-auditable `.ps1` and `.reg` apply/revert script generation, and the Snapshots & Rollback UI page in both heads.
- ⬜ **Task 14 (Elevated `<Name>.Cli` Worker & 1-Click Rollback):** Implement `<Name>.Cli` (`inspect`, `export`, `apply --plan`, `rollback --snapshot`) and wire on-demand UAC `runas` invocation from both UIs with mandatory pre-change snapshot verification and failure rollback.

---

## 📡 6. Concurrent ICMP Diagnostics & Release Verification ![Pending](https://img.shields.io/badge/-pending-red)

📝 Proposed: awaiting user approval.

- ⬜ **Task 15 (Native `iphlpapi.dll` Concurrent Diagnostics):** Implement `IcmpSendEcho` P/Invoke in `.Core` for non-blocking binary-search Path MTU discovery (`IP_FLAG_DF`), concurrent `Task.WhenAll` multi-target latency/jitter/loss sweeps, and async traceroute with `CancellationToken`.
- ⬜ **Task 16 (Diagnostics UI & End-to-End Verification Gate):** Wire the `BDP & Diagnostics` UI in both heads (`Largest MTU`, `Latency`, `Trace`, `Stop`, `Copy`, `Clear`), verify `dotnet test`, `PublishAot` Release build, `net48` Classic build, `evidence_gate.py scan`, and local `CodeRabbit` review.

---

## 📅 2026-11-27: Initial Dual-Head Build, NativeAOT Publish & Live Host-Scan Verification
![Status: In Progress](https://img.shields.io/badge/-in%20progress-yellow)

- 📝 `dotnet test src/FluentNet.Tests/FluentNet.Tests.csproj -c Release`: `5/5` passed (`RECEIPT id=ev-20261126T191047Z-9c86`).
- 📝 `FluentNet.Cli.exe inspect`: live unelevated host scan passed (`RECEIPT id=ev-20261126T191116Z-aa71`).
- 📝 `dotnet publish src/FluentNet.App/FluentNet.App.csproj -c Release -p:Platform=x64 -o temp/publish-aot-x64` (`PublishAot=true`): succeeded (`RECEIPT id=ev-20261126T191459Z-4e2b`).
- 📝 `Invoke-CodeRabbitReview.ps1 -Uncommitted`: installed CodeRabbit CLI `0.9.7`, then hung on `getaddrinfo ENOTFOUND us.i.posthog.com` (telemetry domain blocked by local DNS); task cancelled.
- 📝 `FluentNet.Cli.exe export --profile Optimal --out temp/cli-export`: exported 5 changes to `.ps1` + `.reg` (`RECEIPT id=ev-20261126T191954Z-c4e3`).
- 📝 `FluentNet.Cli.exe diag --host 1.1.1.1`: Path MTU `1480`, Latency `Min=4ms Avg=4.67ms Max=5ms Jitter=1ms Loss=0%`, 8-hop trace reached `1.1.1.1` (`RECEIPT id=ev-20261126T191955Z-12c6`).
