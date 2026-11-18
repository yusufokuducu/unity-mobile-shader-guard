# Unity Mobile Shader Guard: Static HLSL Analyzer & Variant Stripper

[![CI](https://github.com/yusufokuducu/unity-mobile-shader-guard/actions/workflows/ci.yml/badge.svg)](https://github.com/yusufokuducu/unity-mobile-shader-guard/actions/workflows/ci.yml)
[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B%20%7C%20Unity%206-black?logo=unity)](https://unity.com)
[![Version](https://img.shields.io/badge/version-1.2.0-brightgreen.svg)](CHANGELOG.md)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A specialized mobile shader static analysis, UI Toolkit visual heatmap, AI Agent / MCP auto-fix bridge, and build-time variant stripping toolkit for Unity (`2022.3 LTS` and `Unity 6`). Mobile GPUs (**ARM Mali, Qualcomm Adreno, Apple GPU**) rely on Tile-Based Deferred Rendering (TBDR), Hidden Surface Removal (HSR) / Early-Z, and 16-bit (`half` / `real`) vector registers. Desktop-oriented shaders routinely cut mobile ALU occupancy in half, break SRP Batcher compatibility across passes, or bloat player builds with thousands of unused keyword permutations.

---

## Core Capabilities

### 1. Static HLSL / ShaderLab Mobile TBDR Analyzer (`MobileHlslStaticAnalyzer`)
- **Comment, String-Literal & `#if 0` Preprocessor Sanitization**: Extracts `HLSLPROGRAM`/`HLSLINCLUDE`/`CGPROGRAM` blocks and strips comments, dead `#if 0` blocks, and ShaderLab UI strings so token counts reflect actual executable shader code.
- **FP16 vs FP32 Register Pressure**: Calculates the exact ratio of `half`/`real`/`fixed`/`min16float` vs `float` declarations to prevent 32-bit register spilling and warp occupancy drops on ARM Mali & Adreno GPUs.
- **Early-Z / HSR Hazard Detection**: Flags both `clip()`/`discard` instructions and custom fragment depth writes (`SV_Depth`, `SV_DepthGreaterEqual`, `SV_DepthLessEqual`) that disable hardware Hidden Surface Removal on TBDR architectures.
- **In-Loop Texture Fetch & ALU Hazard Detection**: Detects implicit-gradient texture fetches (`SAMPLE_TEXTURE2D`/`tex2D`) inside `for`/`while`/`do` loops (which require `SAMPLE_TEXTURE2D_LOD`), fine screen-space derivatives (`ddx`/`ddy`/`fwidth`), and heavy transcendental ALU calls.
- **Cross-Pass SRP Batcher `UnityPerMaterial` Verification**: Verifies not only that `CBUFFER_START(UnityPerMaterial)` exists, but also checks that **every pass** (`ForwardLit`, `ShadowCaster`, `DepthOnly`, etc.) declares an identical `UnityPerMaterial` layout.
- **Keyword Burst Multiplier & Shorthand Expansion**: Computes permutation risk across `#pragma multi_compile` directives including built-in shorthands (`multi_compile_fog`, `multi_compile_instancing`, `multi_compile_fwdbase`, `multi_compile_shadowcaster`).
- **Quantitative Mobile Readiness Score (`0–100`)** with **Markdown (`.md`)**, **CSV (`.csv`)**, and **MCP JSON (`.json`)** report exporters.

### 2. Modern UI Toolkit Visual Heatmap Dashboard (`MobileShaderGuardWindow`)
- Built with `UnityEngine.UIElements` (`CreateGUI`) with seamless `OnGUI` fallback:
  - **Stacked Severity Distribution Heatmap Bar**: Visual breakdown of `Critical`, `Warning`, `Advisory`, and `Good` shaders across the project.
  - **FP16 Register Ratio & Readiness Gauges**: Visual `ProgressBar` widgets on every shader card plus interactive real-time search (`TextField`) and severity filter buttons.

### 3. MCP (Model Context Protocol) & AI Agent Auto-Fix Bridge (`MobileShaderGuardMcpBridge`)
- Built-in JSON-RPC 2.0 server dispatcher (`HandleJsonRpcRequest`) compatible with **Cursor**, **Claude Desktop**, **Antigravity**, and Unity MCP servers (`protocolVersion: "2024-11-05"`):
  - `shader_guard.audit_shader_source`: Audits inline HLSL/ShaderLab source and returns structured JSON diagnostics.
  - `shader_guard.scan_directory`: Scans any project folder and returns a full JSON heatmap + audit payload.
  - `shader_guard.autofix_shader_source`: Programmatically remediates mobile shader hazards:
    1. Unifies mismatched `CBUFFER_START(UnityPerMaterial)` blocks across passes in canonical property order for SRP Batcher compliance.
    2. Upgrades in-loop `SAMPLE_TEXTURE2D(tex, samp, uv)` calls to `SAMPLE_TEXTURE2D_LOD(tex, samp, uv, 0)`.
    3. Converts artist-toggled `#pragma multi_compile` directives into `#pragma shader_feature_local`.

### 4. Rule-Based Build Variant Stripper (`MobileShaderVariantStripper`)
- Implements `IPreprocessShaders` and `IPostprocessBuildWithReport` driven by a declarative `MobileShaderStripProfile` `ScriptableObject` asset (single keyword presets, co-occurring `ForbiddenKeywordPair` rules, forbidden pass names like `GBuffer`/`Deferred`, and shader whitelisting).
- Outputs a complete JSON audit log (`Logs/MobileShaderGuardStripReport.json`) detailing before/after variant counts per shader.

### 5. Headless CI / Batchmode Support (`MobileShaderGuardCli`)
```bash
unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.MobileShaderGuard.Editor.MobileShaderGuardCli.RunCiAudit
unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.MobileShaderGuard.Editor.MobileShaderGuardCli.RunMcpAuditJson
```

---

## Installation (Unity Package Manager & OpenUPM)

### Via Git URL
Add in **Window -> Package Manager -> + -> Add package from git URL...**:
```text
https://github.com/yusufokuducu/unity-mobile-shader-guard.git
```

### Via OpenUPM CLI
```bash
openupm add com.yusufokuducu.mobile-shader-guard
```

---

## Running Standalone Unit Tests (.NET CLI)

This package includes a zero-dependency NUnit test harness in `DotNetTests~` (ignored by Unity's Asset Importer per UPM convention):

```bash
dotnet test DotNetTests~/MobileShaderGuard.Tests.csproj
```

## License

MIT License — see [LICENSE](LICENSE).
