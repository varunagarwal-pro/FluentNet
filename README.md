<p align="center">
  <img src="assets/fluentnet-banner.png" alt="FluentNet — Modern 64-Bit Windows TCP/IP & Network Optimization Suite" width="100%" />
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10%20%7C%204.8-512BD4?style=for-the-badge&logo=dotnet" alt=".NET 10 | 4.8" /></a>
  <a href="https://learn.microsoft.com/windows/apps/winui/winui3/"><img src="https://img.shields.io/badge/UI-WinUI%203%20%7C%20WPF-0078D4?style=for-the-badge&logo=windows" alt="WinUI 3 | WPF" /></a>
  <img src="https://img.shields.io/badge/Arch-x64%20%7C%20arm64-2ea44f?style=for-the-badge" alt="x64 | arm64" />
  <img src="https://img.shields.io/badge/Telemetry-Zero%20%7C%20No%20Bloat-red?style=for-the-badge" alt="Zero Telemetry" />
  <a href="docs/ARCHITECTURE.md"><img src="https://img.shields.io/badge/Docs-Architecture-0969da?style=for-the-badge" alt="Architecture Docs" /></a>
</p>

# ⚡ FluentNet

**FluentNet** is a modern, open-source, strictly **64-bit** (`x64` / `arm64`) Windows 10 & Windows 11 TCP/IP and network adapter optimization suite—rebuilt from scratch as a clean-room successor to SpeedGuide's classic **SG TCP Optimizer**.

---

## 🎯 What & Why

Classic TCP tuning utilities were built for Windows XP/7 (`32-bit` x86, unsigned, `ICMP.DLL`, Win9x `VxD\MSTCP` keys, IE `FeatureControl`, and slow per-setting `powershell.exe` spawns). **FluentNet** modernizes the entire stack for Windows 10 and Windows 11:

- **Zero Snake-Oil, OS-Build Aware:** Drops deprecated tweaks (`TCP Chimney`, `NetDMA`, Win9x/IE keys) and supports modern Windows 10/11 primitives (`CUBIC`, `CTCP`, `BBR2` with Win11 24H2+ `loopbacklargemtu` safety guard, `TCP Fast Open`, `RSS`/`RSC`/`LSO`/`USO`, per-NIC `NetAdapterAdvancedProperty`, and `AFD`).
- **64-Bit Parallel Host-Scan Engine:** Probes OS build, CPU cores, active NICs, CIM/WMI (`ROOT\StandardCimv2`), and 64-bit Registry concurrently off the UI thread to recommend tailored **Optimal** settings.
- **Safety First (Zero Blind Writes):**
  - Runs **`asInvoker` (unelevated)** by default for inspection, BDP math, Path MTU/latency diagnostics, and script export.
  - Shows a **transparent Diff Preview** (`Current → Proposed`, `Windows Default`, exact path/command, reboot flag) before any change.
  - Captures a **mandatory timestamped JSON snapshot** before applying and supports **1-click rollback** plus exportable `.ps1` / `.reg` scripts.
- **Zero Telemetry, Zero Adware:** 100% local, auditable, and open source.

---

## 🏗️ Architecture Summary

FluentNet uses a **dual-head 64-bit architecture** sharing a single NativeAOT-ready core engine:

| Component | Target | Purpose |
|---|---|---|
| **`FluentNet.Core`** | `.NET 10` (`x64;arm64`) | Parallel Host Scanner, CIM/WMI & 64-bit Registry providers, BDP calculator, Diff engine, JSON snapshots, `.ps1`/`.reg` exporter, and `iphlpapi.dll` ICMP probes. |
| **`FluentNet.App`** | `.NET 10` **WinUI 3** (Unpackaged, NativeAOT) | Flagship Windows 11 Fluent UI (Mica, `NavigationView`, `SettingsCard`, `ContentDialog`). |
| **`FluentNet.Classic`** | **`.NET 4.8` WPF** (`x64`, Zero-NuGet) | Lightweight, zero-dependency portable `.exe` for clean Windows 10/11, Server, or OOBE environments—compiling shared `.Core` `.cs` files directly. |
| **`FluentNet.Cli`** | `.NET 10` (`x64;arm64`) | Headless CLI & on-demand UAC-elevated worker for Apply and Rollback. |

📖 **Read the full specification in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).**

---

## ✨ Key Capabilities

- **Adaptive Profiles:** `Windows Default`, `Current`, `Recommended Optimal` (host-scanned), `Gaming / Low-Latency`, and `Custom`.
- **Per-NIC Driver Tuning:** Dynamically surfaces driver-exposed `NetAdapterAdvancedProperty` keys (`*InterruptModeration`, `*EEE`, `*FlowControl`, `*NumRssQueues`, `*JumboPacket`) and per-interface Nagle (`TcpAckFrequency`, `TCPNoDelay`, `TcpDelAckTicks`).
- **Async ICMP Diagnostics:** Non-blocking binary-search **Path MTU Discovery** (`DF` bit), concurrent **multi-target latency/jitter/loss sweeps**, **traceroute**, and **BDP/RWIN** calculator.
- **Full Auditability:** Export any change plan or rollback snapshot as standalone `.ps1` and `.reg` files.

---

## 🛠️ Building from Source

```powershell
# Run unit tests
dotnet test src/FluentNet.Tests/FluentNet.Tests.csproj -c Release

# Build Classic Portable Edition (.NET 4.8 WPF, x64)
dotnet build src/FluentNet.Classic/FluentNet.Classic.csproj -c Release -p:Platform=x64

# Publish WinUI 3 Flagship (Unpackaged NativeAOT, x64)
dotnet publish src/FluentNet.App/FluentNet.App.csproj -c Release -p:Platform=x64
```

---

## 🤝 Contributing & Governance

- Pull requests auto-trigger **CodeRabbit** AI reviews (`.coderabbit.yaml`) and **GitHub Actions CI** (`ci.yml`, `codeql.yml`).
- Use the provided [Bug Report](.github/ISSUE_TEMPLATE/bug_report.yml), [Feature Request](.github/ISSUE_TEMPLATE/feature_request.yml), and [Pull Request](.github/PULL_REQUEST_TEMPLATE.md) templates.
