# 🏗️ FluentNet — System Architecture

> **Document Authority:** Canonical architecture specification for **FluentNet** (`D:\SentinelLabs\FluentNet\`).  
> **Target Platforms:** Windows 10 (Build 19041+) & Windows 11 (`x64` & `arm64`)  
> **Architecture Standard:** 64-bit only, multi-core async concurrency, zero telemetry, zero snake-oil.

---

## 1. Design Philosophy & Core Invariants

**FluentNet** is a ground-up, clean-room rebuild of SpeedGuide's classic SG TCP Optimizer for modern Windows 10 and Windows 11. It replaces legacy 32-bit x86 MFC/PowerShell-spawning patterns with a **strictly 64-bit, parallel, OS-aware architecture**:

1. **Zero UI-Thread Blocking:** All host scans, CIM/WMI queries, Registry reads, Path MTU binary searches, and multi-target ICMP sweeps execute off the UI thread using `Task.WhenAll`, `Parallel.ForEachAsync`, and `CancellationToken`.
2. **Least-Privilege (`asInvoker`) by Default:** Both UI heads launch unelevated. Reading live state, running diagnostics, calculating BDP, and exporting `.ps1`/`.reg` scripts require **no UAC prompt**.
3. **Mandatory Pre-Change Snapshot & Rollback:** Every mutation writes and verifies a timestamped JSON snapshot before touching a single Registry key, CIM property, or `netsh` setting.
4. **Transparent Diff Gate:** Users always inspect `Current → Proposed` alongside `Windows Default`, the exact underlying path/command, and reboot impact before applying.
5. **No Legacy Snake-Oil:** Deprecated Win9x/XP/IE/Win8 settings (`VxD\MSTCP`, IE `FeatureControl`, `TCP Chimney Offload`, `NetDMA`, static `GlobalMaxTcpWindowSize`, `ICMP.DLL`) are excluded.

---

## 2. Dual-Head Solution Topology

```mermaid
graph TD
    subgraph "UI Heads (asInvoker / Unelevated)"
        APP["FluentNet.App<br/>WinUI 3 (.NET 10, NativeAOT, x64;arm64)<br/>Unpackaged Windows 11 Fluent UI"]
        CLASSIC["FluentNet.Classic<br/>WPF (.NET 4.8, x64, Zero-NuGet)<br/>Portable Single-EXE Edition"]
    end

    subgraph "Shared 64-Bit Engine"
        CORE["FluentNet.Core (.NET 10)<br/>Shared via linked .cs files to Classic<br/>• HostScanner (Parallel 6-Signal Probe)<br/>• CimTcpProvider (ROOT\StandardCimv2)<br/>• Registry64Provider (HKLM)<br/>• ProfileEngine & BdpCalculator<br/>• DiffEngine & ScriptExporter (.ps1/.reg)<br/>• SnapshotManager (JSON) & IcmpProbe (iphlpapi.dll)"]
    end

    subgraph "Privileged Execution & Verification"
        CLI["FluentNet.Cli (.NET 10)<br/>Headless CLI & On-Demand UAC (runas) Worker"]
        TESTS["FluentNet.Tests (xUnit)<br/>Unit & Contract Verification"]
    end

    APP --> CORE
    CLASSIC -. linked .cs .-> CORE
    CLI --> CORE
    TESTS --> CORE
    APP -- "ShellExecuteEx (runas) on Apply/Rollback" --> CLI
    CLASSIC -- "ShellExecuteEx (runas) on Apply/Rollback" --> CLI
```

| Project | Target Framework | Architecture | Role |
|---|---|---|---|
| **`FluentNet.Core`** | `net10.0-windows10.0.19041.0` | `x64;arm64` | NativeAOT-safe (`System.Text.Json` source-gen, blittable P/Invoke) networking, host-scan, diff, snapshot, and ICMP engine. |
| **`FluentNet.App`** | `net10.0-windows10.0.19041.0` | `x64;arm64` | Flagship WinUI 3 Unpackaged (`WindowsPackageType=None`, `SelfContained=true`, `PublishAot=true`, excluding `WindowsAppSDK.AI`/`ML`). |
| **`FluentNet.Classic`** | `net48` (`UseWPF=true`) | `x64` | Zero-dependency portable WPF `.exe` for clean Windows 10/11, Server, or OOBE environments; compiles shared `.Core` `.cs` files directly. |
| **`FluentNet.Cli`** | `net10.0-windows10.0.19041.0` | `x64;arm64` | Headless CLI (`inspect`, `export`, `apply --plan`, `rollback --snapshot`) and on-demand UAC-elevated worker. |
| **`FluentNet.Tests`** | `net10.0-windows10.0.19041.0` | `x64` | Automated `xUnit` test suite for BDP math, profiles, diffing, script export, snapshots, and Path MTU binary search. |

---

## 3. Native Windows Stack Integration (Replacing `powershell.exe` Spawns)

Instead of launching `powershell.exe` per setting, `FluentNet.Core` interacts directly with Windows subsystems:

1. **CIM / WMI (`ROOT\StandardCimv2`):**
   - `MSFT_NetTCPSetting` (read `Internet`, `InternetCustom`, `Datacenter`, `DatacenterCustom`, `Compat`)
   - `MSFT_NetOffloadGlobalSetting` (`ReceiveSideScaling`, `ReceiveSegmentCoalescing`)
   - `MSFT_NetAdapterLsoSettingData`, `MSFT_NetAdapterChecksumOffloadSettingData`, `MSFT_NetAdapterRssSettingData`, `MSFT_NetAdapterRscSettingData`, `MSFT_NetAdapterUsoSettingData`
   - `MSFT_NetAdapterAdvancedPropertySettingData` (driver-exposed `*InterruptModeration`, `*EEE`, `*FlowControl`, `*NumRssQueues`, `*JumboPacket`)
2. **`netsh` Supplemental & Interface Layer:**
   - Used where Windows 10/11 client editions mark `Set-NetTCPSetting` properties read-only (`netsh int tcp set supplemental template=<T> congestionprovider=<CUBIC|CTCP|BBR2>`).
   - **Win11 24H2/25H2 BBR2 Safety Guard:** Automatically pairs `BBR2` with `netsh int ipv4/ipv6 set global loopbacklargemtu=disable` to prevent localhost/loopback stalls.
3. **64-Bit Registry (`RegistryView.Registry64`):**
   - `Tcpip\Parameters`, `Tcpip\Parameters\Interfaces\{GUID}` (per-NIC `TcpAckFrequency`, `TCPNoDelay`, `TcpDelAckTicks`), `Tcpip\ServiceProvider`, `Multimedia\SystemProfile`, `Policies\Microsoft\Windows\Psched`, `Tcpip\QoS`, and `AFD\Parameters`.
4. **Native ICMP (`iphlpapi.dll`):**
   - `IcmpCreateFile` / `IcmpSendEcho` / `IcmpCloseHandle` with `IP_FLAG_DF` for non-blocking binary-search Path MTU discovery (68–1500+ bytes), concurrent multi-target latency/jitter sweeps, and TTL-incrementing traceroute.

---

## 4. 6-Signal Host-Scan & Adaptive Recommendation Pipeline

1. **OS Build & Edition Probe:** Gates Win10 vs Win11 22H2/24H2/25H2 features (`BBR2`, `FastOpen`, `LoopbackLargeMtu`).
2. **CPU Topology Probe:** Physical core count for RSS queue recommendations.
3. **Active NIC & Media Probe:** Distinguishes Wired Ethernet, Wi-Fi, and VPN/Virtual adapters, link speed, IPv4/IPv6, MTU, and driver-supported advanced properties.
4. **Optional Live Path MTU / Baseline RTT Probe:** Detects PPPoE (`1492`) and feeds BDP/RWIN calculations.
5. **State Drift Audit:** Compares live settings against `Windows Default` and `Recommended Optimal`.
6. **Auditable Change Plan:** Emits exact `Current → Proposed` diffs, reboot flags, and exportable `.ps1`/`.reg` scripts.

---

## 5. End-to-End Execution & UI Workflow

- **5-Step Flow:** `Launch (asInvoker)` → `Parallel Host Scan` → `Profile / Custom Tuning & Diagnostics` → `Diff Preview Modal (Export .ps1/.reg)` → `Mandatory JSON Snapshot + Elevated Cli Apply / 1-Click Rollback`.
- **UI Surfaces:** `Dashboard (Host Scan)`, `General Settings`, `Advanced Settings` (grouped expanders + dynamic NIC driver properties), `BDP & Diagnostics`, `Snapshots & Rollback`, and `Diff Preview Modal`.
