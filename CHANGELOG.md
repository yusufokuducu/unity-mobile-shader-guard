# Changelog

All notable changes to `com.yusufokuducu.mobile-shader-guard` are documented in this file.

## [1.2.0] - 2026-11-18

### Added
- **Modern UI Toolkit (`CreateGUI`) Visual Heatmap Dashboard**:
  - Upgraded `MobileShaderGuardWindow` with a UI Toolkit visual tree (`VisualElement`, `ProgressBar`, color-coded stacked severity distribution heatmap bar, per-shader FP16 register ratio gauge, and interactive search + severity filter buttons) while maintaining dual-compatibility with legacy `OnGUI`.
- **MCP (Model Context Protocol) & AI Agent JSON-RPC 2.0 Bridge (`MobileShaderGuardMcpBridge`)**:
  - Full JSON-RPC 2.0 server dispatcher (`HandleJsonRpcRequest`) supporting `initialize`, `tools/list`, and `tools/call` for AI coding assistants (Cursor, Claude, Antigravity).
  - Implemented `shader_guard.audit_shader_source`, `shader_guard.scan_directory`, and `shader_guard.autofix_shader_source` tools.
  - Added automated HLSL remediation (`AutoFixShaderSource`) that unifies mismatched `CBUFFER_START(UnityPerMaterial)` blocks across passes, upgrades in-loop `SAMPLE_TEXTURE2D` calls to `SAMPLE_TEXTURE2D_LOD(..., 0)`, and converts custom `#pragma multi_compile` directives to `#pragma shader_feature_local`.
  - Added `MobileShaderGuardCli.RunMcpAuditJson()` batchmode entrypoint exporting `Logs/MobileShaderGuardMcpReport.json`.
- **GitHub Actions CI & OpenUPM Metadata**:
  - Added `.github/workflows/ci.yml` running the standalone `.NET 8` NUnit test suite on every push and pull request.
  - Added OpenUPM-compliant metadata (`documentationUrl`, `changelogUrl`, `licensesUrl`) to `package.json`.

## [1.1.0] - 2026-11-18

### Added
- Single-pass lexical comment & string-literal sanitizer (`SanitizeCommentsAndStrings`) that preserves URLs inside ShaderLab property descriptions.
- Preprocessor dead-branch stripper (`StripDisabledPreprocessorBlocks`) for `#if 0` / `#if false` blocks with `#else` / `#elif` support.
- `do { ... } while(...)` loop detection and nested loop span merging to prevent double-counting in-loop texture fetches.

## [1.0.0] - 2026-11-18

### Added
- Initial release of `MobileHlslStaticAnalyzer`, `MobileShaderVariantStripper`, `MobileShaderStripProfile`, `MobileShaderGuardWindow`, and `MobileShaderGuardCli`.
