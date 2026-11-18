# Unity Mobile Shader Guard: Static HLSL Analyzer & Variant Stripper

[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B%20%7C%20Unity%206-black?logo=unity)](https://unity.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A specialized mobile shader static analysis and build-time variant stripping toolkit for Unity (`2022.3 LTS` and `Unity 6`). Mobile GPUs (**ARM Mali, Qualcomm Adreno, Apple GPU**) rely on Tile-Based Deferred Rendering (TBDR), Hidden Surface Removal (HSR) / Early-Z, and 16-bit (`half` / `real`) vector registers. Desktop-oriented shaders routinely cut mobile ALU occupancy in half, break SRP Batcher compatibility across passes, or bloat player builds with thousands of unused keyword permutations.

---

## Core Capabilities

### 1. Static HLSL / ShaderLab Mobile TBDR Analyzer (`MobileHlslStaticAnalyzer`)
- **Comment & String-Literal Sanitization**: Extracts `HLSLPROGRAM`/`HLSLINCLUDE`/`CGPROGRAM` blocks and strips comments and ShaderLab UI strings so token counts reflect actual executable shader code.
- **FP16 vs FP32 Register Pressure**: Calculates the exact ratio of `half`/`real`/`fixed`/`min16float` vs `float` declarations to prevent 32-bit register spilling and warp occupancy drops on ARM Mali & Adreno GPUs.
- **Early-Z / HSR Hazard Detection**: Flags both `clip()`/`discard` instructions and custom fragment depth writes (`SV_Depth`, `SV_DepthGreaterEqual`, `SV_DepthLessEqual`) that disable hardware Hidden Surface Removal on TBDR architectures.
- **In-Loop Texture Fetch & ALU Hazard Detection**: Detects implicit-gradient texture fetches (`SAMPLE_TEXTURE2D`/`tex2D`) inside `for`/`while` loops (which require `SAMPLE_TEXTURE2D_LOD`), fine screen-space derivatives (`ddx`/`ddy`/`fwidth`), and heavy transcendental ALU calls.
- **Cross-Pass SRP Batcher `UnityPerMaterial` Verification**: Verifies not only that `CBUFFER_START(UnityPerMaterial)` exists, but also checks that **every pass** (`ForwardLit`, `ShadowCaster`, `DepthOnly`, etc.) declares an identical `UnityPerMaterial` layout.
- **Keyword Burst Multiplier & Shorthand Expansion**: Computes permutation risk across `#pragma multi_compile` directives including built-in shorthands (`multi_compile_fog`, `multi_compile_instancing`, `multi_compile_fwdbase`, `multi_compile_shadowcaster`).
- **Quantitative Mobile Readiness Score (`0–100`)** with **Markdown (`.md`)** and **CSV (`.csv`)** report exporters.

### 2. Rule-Based Build Variant Stripper (`MobileShaderVariantStripper`)
- Implements `IPreprocessShaders` and `IPostprocessBuildWithReport`.
- Driven by a declarative `MobileShaderStripProfile` `ScriptableObject` asset:
  - **Single Keyword Presets**: Strips `DEBUG_DISPLAY`, `EDITOR_VISUALIZATION`, `_MAIN_LIGHT_SHADOWS_CASCADE`, `_ADDITIONAL_LIGHT_SHADOWS`, `_SHADOWS_SOFT*`, `_SCREEN_SPACE_OCCLUSION`, `LIGHTMAP_BICUBIC_SAMPLING`, `_DBUFFER_MRT*`, `_GBUFFER_NORMALS_OCT`, etc.
  - **Co-Occurring Keyword Pair Stripping (`ForbiddenKeywordPair`)**: Allows individual expensive features in isolation (e.g., cascade shadows OR additional light shadows) while stripping variants where both are active simultaneously.
  - **Pass Name Stripping**: Strips entire desktop-only passes (`GBuffer`, `Deferred`) on mobile targets.
  - **Shader Whitelisting**: Protects essential UI/Sprite/Error shaders from stripping.
- Outputs a complete JSON audit log (`Logs/MobileShaderGuardStripReport.json`) detailing before/after variant counts per shader.

### 3. Headless CI / Batchmode Support (`MobileShaderGuardCli`)
Run automated shader audits in CI/CD pipelines:
```bash
unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.MobileShaderGuard.Editor.MobileShaderGuardCli.RunCiAudit
```

---

## Installation (Unity Package Manager)

Add via Git URL in **Window -> Package Manager -> + -> Add package from git URL...**:

```text
https://github.com/yusufokuducu/unity-mobile-shader-guard.git
```

---

## Running Standalone Unit Tests (.NET CLI)

This package includes a zero-dependency NUnit test harness in `DotNetTests~` (ignored by Unity's Asset Importer per UPM convention):

```bash
dotnet test DotNetTests~/MobileShaderGuard.Tests.csproj
```

## License

MIT License — see [LICENSE](LICENSE).
