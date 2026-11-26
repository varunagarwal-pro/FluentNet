# 🏷️ FluentNet — Unified SemVer Versioning Policy

> **Single Source of Truth:** [`version.txt`](../version.txt) → [`src/Directory.Build.props`](../src/Directory.Build.props)  
> **Format:** `MAJOR.MINOR.PATCH` (e.g. `0.1.0` → `0.1.1` → `0.2.0` → `1.0.0`)

---

## 1. How Versioning Works

Every project in `src/` (`FluentNet.Core`, `FluentNet.App`, `FluentNet.Classic`, `FluentNet.Cli`) reads [`version.txt`](../version.txt) automatically at build time via [`src/Directory.Build.props`](../src/Directory.Build.props):

- `<Version>` = `MAJOR.MINOR.PATCH`
- `<AssemblyVersion>` = `MAJOR.MINOR.PATCH.0`
- `<FileVersion>` = `MAJOR.MINOR.PATCH.0`

There are **zero** hardcoded version numbers in `.csproj` files—eliminating version drift across editions.

---

## 2. Mandatory Agent Auto-Bump Rule

AI agents working on `FluentNet` **MUST** bump [`version.txt`](../version.txt) automatically after any code or packaging change (without waiting for the user to ask) before running [`tools/Publish-Local.ps1`](../tools/Publish-Local.ps1):

| Change Type | Bump Part | Example |
|---|---|---|
| Bug fix, UI tweak, refactor, docs/CI update affecting shipped assets | `patch` | `0.1.0` → `0.1.1` |
| New feature, new diagnostic, new parameter/profile support | `minor` | `0.1.1` → `0.2.0` |
| Breaking CLI/snapshot schema or major architectural milestone | `major` | `0.2.0` → `1.0.0` |

### Automated Bump Command
```powershell
# Bump patch (default), minor, or major in version.txt:
powershell -ExecutionPolicy Bypass -File tools/Bump-Version.ps1 -Part patch

# Build, test, and publish all 3 executables to D:\Program\FluentNet:
powershell -ExecutionPolicy Bypass -File tools/Publish-Local.ps1
```

*(Or pass `-Bump patch|minor|major` directly to `tools/Publish-Local.ps1` once wired.)*
