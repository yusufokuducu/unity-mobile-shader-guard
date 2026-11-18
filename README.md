# Unity Mobile Shader Guard: Static HLSL Analyzer & Variant Stripper

[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B%20%7C%20Unity%206-black?logo=unity)](https://unity.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A dedicated mobile shader optimization and build-time variant stripping toolkit for Unity (`2022.3 LTS` and `Unity 6`). Mobile GPUs (ARM Mali, Qualcomm Adreno, Apple GPU) rely on Tile-Based Deferred Rendering (TBDR), Hidden Surface Removal (HSR) / Early-Z, and 16-bit (`half` / `real`) vector registers. Desktop-oriented shaders routinely cut mobile ALU occupancy in half or bloat player builds with thousands of unused keyword permutations.

---

## Core Capabilities

1. **Static HLSL / ShaderLab Mobile Analyzer (`MobileHlslStaticAnalyzer`)**
   - Scans `.shader`, `.hlsl`, and `.cginc` files in seconds.
   - **FP16 vs FP32 Register Pressure**: Calculates the exact ratio of `half`/`real`/`fixed` vs `float` declarations to prevent ALU register spilling on mobile GPUs.
   - **Early-Z / HSR Hazard Detection**: Flags `clip()` and `discard` instructions that disable hardware Hidden Surface Removal on TBDR architectures.
   - **SRP Batcher Compliance**: Verifies `CBUFFER_START(UnityPerMaterial)` declarations.
   - **Keyword Burst Multiplier Scoring**: Computes the permutation explosion risk across `#pragma multi_compile` directives.
2. **Rule-Based Build Variant Stripper (`MobileShaderVariantStripper`)**
   - Implements `IPreprocessShaders` and `IPostprocessBuildWithReport`.
   - Driven by a declarative `MobileShaderStripProfile` ScriptableObject asset.
   - Automatically strips unused desktop/debug URP keywords (`DEBUG_DISPLAY`, `_MAIN_LIGHT_SHADOWS_CASCADE`, `_ADDITIONAL_LIGHT_SHADOWS`, `_SHADOWS_SOFT*`, `_DBUFFER_MRT*`, etc.) on Android & iOS builds.
   - Outputs a complete JSON audit log (`Logs/MobileShaderGuardStripReport.json`) detailing before/after variant counts per shader.

---

## Installation (Unity Package Manager)

Add via Git URL in **Window -> Package Manager -> + -> Add package from git URL...**:

```text
https://github.com/yusufokuducu/unity-mobile-shader-guard.git
```

---

## Usage

1. Open **Window -> Analysis -> Mobile Shader Guard & HLSL Analyzer**.
2. Click **Scan All Project Shaders & HLSL** to audit every shader in `Assets/` and filter by severity (`Good`, `Advisory`, `Warning`, `Critical`).
3. Click **Create / Select Strip Profile Asset** to generate `Assets/MobileShaderStripProfile.asset` and customize which keyword families are stripped during Android/iOS builds.

## License

MIT License — see [LICENSE](LICENSE).
