# FluentNet Brand & Visual Asset Guide (`v2`)

## 1. What's Included in This Drop-In Package

Copy the `assets/` folder directly over your repository's `assets/` directory — all filenames match existing `FluentNet.App.csproj`, `FluentNet.Classic.csproj`, and `README.md` paths for a zero-conflict merge.

| Asset Path | Resolution / Format | Enhancement Summary |
|---|---|---|
| `assets/fluentnet-icon.png` | 1024×1024 RGBA PNG | Upgraded studio-grade dark-navy squircle + cyan/emerald intersecting waves & 3 luminous nodes on 100% transparent RGBA (`CONSTRAINTS.md` compliant) |
| `assets/AppIcon.ico` | Multi-res ICO (16–256px) | Rebuilt with all 7 Windows shell sizes (`16, 24, 32, 48, 64, 128, 256`) from the new 1024px master |
| `assets/svg/fluentnet-icon.svg` | Vector SVG | New scalable master vector source for Figma, web, and high-DPI exports |
| `assets/Square44x44Logo.png`, `Square150x150Logo.png`, `StoreLogo.png` | RGBA PNG | Regenerated from the new 1024px master |
| `assets/Square310x310Logo.png`, `Wide310x150Logo.png` | RGBA PNG | New Windows Start / Store large & wide tile assets |
| `assets/fluentnet-banner.png` | 1280×640 PNG | Flagship Dark Mode README hero banner with Mica glass telemetry visual |
| `assets/fluentnet-banner-light.png` | 1280×640 PNG | New Light Mode README hero banner for GitHub `<picture>` theme auto-switching |
| `assets/github-social-preview.png` | 1280×640 PNG | Ready-to-upload GitHub Repository Social Preview (Settings → Social preview) |
| `assets/icons/*.png` (8 icons) | 256×256 RGBA PNG | 4× higher resolution than legacy 64×64 bitmaps; redesigned with crisp Fluent 11 gradients, authentic RJ45 Ethernet jack, high-contrast Wi-Fi 7 arcs, precision radar diagnostics, and clean snapshots restore arc — legible on both Dark & Light Mica |

## 2. Color Tokens (Dark & Light Parity)

| Role | Dark Mode Hex | Light Mode Hex | Usage |
|---|---|---|---|
| **Primary Accent** | `#38BDF8` | `#0284C7` | Primary buttons, active wave, proposed diff highlight |
| **Secondary / Healthy** | `#34D399` | `#059669` | UP badges, aligned state, emerald wave |
| **Warning / Gaming** | `#FBBF24` | `#D97706` | Reboot badges, Gaming/Advanced highlights |
| **Accent Violet** | `#A78BFA` | `#7C3AED` | Snapshots, driver properties, secondary tiles |
| **Brand Navy Base** | `#0C1936` → `#0A2F59` | `#0C1936` → `#0A2F59` | App icon squircle backdrop |

## 3. GitHub README Dual-Theme Banner Snippet

Replace line 2 of `README.md` with:

```html
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/fluentnet-banner.png" />
  <source media="(prefers-color-scheme: light)" srcset="assets/fluentnet-banner-light.png" />
  <img src="assets/fluentnet-banner.png" alt="FluentNet — Modern 64-Bit Windows TCP/IP & Network Optimizer" width="100%" />
</picture>
```

## 4. Minor `.csproj` Cleanup Note

In `src/FluentNet.App/FluentNet.App.csproj` line 41, you can fix the typo `Exclude="..\..\assets\Assetsa\**"` to `Exclude="..\..\assets\BRAND_GUIDE.md;..\..\assets\svg\**;..\..\assets\fluentnet-banner*.png;..\..\assets\github-social-preview.png"` so README/marketing banners and SVGs aren't copied into the published app binary folder (saving ~1.5 MB on publish!).
