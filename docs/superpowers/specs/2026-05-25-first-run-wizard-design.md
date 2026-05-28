# First-Run Configuration Wizard

> **状态：已完成 (2026-05-27)** — 所有功能已实现

## Summary

Eliminate hardcoded paths and pre-filled API keys. On first run (no `paths.json`), launch an interactive console wizard that collects Storage path + model API credentials, then auto-generates all required config files from templates.

## Current Problems

1. `paths.json` hardcodes `E:/Workspace/AgentLilaraProject/Storage` — breaks when moving devices
2. All `Core/*.json` contain real API keys in plaintext — can't be shared or versioned
3. No first-run experience — missing configs cause cryptic crashes instead of guided setup

## Design

### Template System

Templates live in `templates/` next to the binary. Each contains placeholder markers for sensitive fields:

```
AgentCoreProcessor/
├── templates/
│   ├── Core/
│   │   ├── Base.json
│   │   ├── ExpressCore.json
│   │   ├── WorkingCore.json
│   │   ├── SystemCore.json
│   │   ├── SleepTalkCore.json
│   │   ├── SubAgentCore.json
│   │   ├── CombineCore.json
│   │   ├── ConsolidationCore.json
│   │   ├── ConsolidationFinalCore.json
│   │   ├── DedupCore.json
│   │   ├── LinkCore.json
│   │   ├── MemoryExtractionCore.json
│   │   ├── MemoryQueryCore.json
│   │   ├── ReviewCore.json
│   │   ├── SummarizationCore.json
│   │   ├── WeightCore.json
│   │   ├── EmbeddingProvider.json
│   │   ├── VisionProvider.json
│   │   ├── OcrProvider.json
│   │   └── Persona.txt
│   ├── Engine/
│   │   ├── EngineConfig.json
│   │   ├── ImpulseConfig.json
│   │   ├── SignalFilter.json
│   │   ├── ToolProfiles.json
│   │   ├── TrustProgressionConfig.json
│   │   └── VisionEngineConfig.json
│   ├── Dream/
│   │   └── DreamConfig.json
│   ├── Command/
│   │   └── CommandConfig.json
│   └── WebUI/
│       └── WebConfig.json
```

### Placeholder Convention

Template JSONs use `{{PLACEHOLDER_NAME}}` markers. The wizard replaces them with collected values:

| Placeholder | Source | Applied To |
|---|---|---|
| `{{HEAVY_API_KEY}}` `{{HEAVY_ENDPOINT}}` `{{HEAVY_MODEL}}` `{{HEAVY_PROVIDER}}` | Q2 | WorkingCore.json |
| `{{GENERAL_API_KEY}}` `{{GENERAL_ENDPOINT}}` `{{GENERAL_MODEL}}` `{{GENERAL_PROVIDER}}` | Q3 | ExpressCore, SystemCore, SubAgentCore |
| `{{LIGHT_API_KEY}}` `{{LIGHT_ENDPOINT}}` `{{LIGHT_MODEL}}` `{{LIGHT_PROVIDER}}` | Q4 | Base, SleepTalkCore, all memory Cores |
| `{{EMBEDDING_API_KEY}}` `{{EMBEDDING_ENDPOINT}}` `{{EMBEDDING_MODEL}}` `{{EMBEDDING_ENABLED}}` | Q5 | EmbeddingProvider.json |
| `{{VISION_API_KEY}}` `{{VISION_ENDPOINT}}` `{{VISION_MODEL}}` `{{VISION_ENABLED}}` | Q6 | VisionProvider.json |
| `{{OCR_API_KEY}}` `{{OCR_ENDPOINT}}` `{{OCR_MODEL}}` `{{OCR_ENABLED}}` | Q7 | OcrProvider.json |

### Model Tier → Core Mapping

| Tier | Wizard Label | Cores |
|---|---|---|
| Heavy | 主力模型 (Working) | WorkingCore |
| General | 泛用模型 (日常对话) | ExpressCore, SystemCore, SubAgentCore |
| Light | 轻量模型 (后台任务) | Base, SleepTalkCore, CombineCore, ConsolidationCore, ConsolidationFinalCore, DedupCore, LinkCore, MemoryExtractionCore, MemoryQueryCore, ReviewCore, SummarizationCore, WeightCore |

### Provider Selection

Provider is a binary choice per tier:
- `claude` → Anthropic Messages API format (native tool_use, prompt caching, extended thinking)
- `openai` → OpenAI Chat Completions API format

### Auxiliary Services

Each auxiliary service (Embedding, Vision, OCR) can be individually enabled/disabled. Config JSON includes an `enabled` field. When disabled, MasterEngine skips provider initialization.

Embedding/Vision providers auto-generate their endpoint sub-paths:
- Embedding: `{endpoint}` (user provides final URL)
- Vision: `{endpoint}` (user provides final URL)
- OCR: `{endpoint}` (user provides final URL)

Default models for aux services:
- Embedding: `BAAI/bge-large-zh-v1.5`
- Vision: `Qwen/Qwen3-VL-8B-Instruct`
- OCR: `deepseek-ai/DeepSeek-OCR`

### Wizard Flow

```
PathConfig.Load() → paths.json missing → SetupWizard.Run()

[1/7] Storage path (default: .\Storage)
[2/7] Heavy model: apiKey, endpoint, model, Claude? (y/N)
[3/7] General model: apiKey, endpoint, model, Claude? (y/N)
[4/7] Light model: apiKey, endpoint, model, Claude? (y/N)
[5/7] Embedding: enable? (Y/n), apiKey, endpoint, model
[6/7] Vision: enable? (Y/n), apiKey, endpoint, model
[7/7] OCR: enable? (Y/n), apiKey, endpoint, model
→ Preview → Confirm → Release templates → Write paths.json → Continue startup
```

### Items NOT Included

- Adapter configs (user configures later via WebUI or manual edit)
- SSH/MCP (not yet implemented)
- Persona.txt (released as-is from template, no placeholder replacement)
- Engine tuning params (released with current defaults, user edits manually if needed)

### Code Changes

| File | Change |
|---|---|
| `Config/PathConfig.cs` | `Load()` calls `SetupWizard.Run()` when `paths.json` missing |
| `Config/SetupWizard.cs` | **NEW** — 7-step console Q&A, preview, confirm |
| `Config/TemplateReleaser.cs` | **NEW** — read template JSON, replace placeholders, write to storage |
| `templates/` | **NEW** — all template files with placeholders |
| `Engine/Core/MasterEngine.cs` | Check `enabled` field before creating aux providers |
| `Client/EmbeddingProvider.cs` | Add `Enabled` property, load from config |
| `Client/SiliconFlowVisionProvider.cs` | Add `Enabled` property, load from config |
| `Client/SiliconFlowOcrProvider.cs` | Add `Enabled` property, load from config |
| `AgentCoreProcessor.csproj` | Add `<None Update="templates\**\*">` for publish copy |

### Files NOT Generated (runtime-only)

These are created automatically at runtime and don't need templates:
- `paths.json` (written by wizard)
- `Database/*` (SQLite files + schema markers)
- `ChannelContexts/*`, `ChannelState/*` (runtime state)
- `SystemLoop/*` (runtime state)
- `Logs/*` (accumulated at runtime)
- `PluginData/*` (runtime data)
- `Images/*` (cached images)
- `FileAdapter/*` (test adapter I/O)
- `Dream/DreamHistory.json`, `Dream/DreamStats.json` (accumulated runtime data)
- `MCP/McpServers.json` (empty default, generated by MCP system)
- `Core/Backup/*` (auto backups during provider switch)
- `Core/SelfKnowledge/*` (auto-generated self-knowledge)
