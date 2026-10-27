# 多 Agent Token 统计 — 调研结论与实现设计（v1.2.0）

## 一、本机各 Agent 客户端的数据源（2026-10-27 实测）

| Agent | 数据源 | 格式 | token 明细 | 模型字段 | 时间戳 |
|---|---|---|---|---|---|
| **opencode**（已支持） | `~/.local/share/opencode/opencode.db` → `session_message` | SQLite | `data.tokens.{input,output,reasoning,cache.read,cache.write}` | `data.model.id` / `data.model.providerID` | ms epoch |
| **zcode** | `~/.zcode/cli/db/db.sqlite` → `model_usage` | SQLite | `input_tokens, output_tokens, reasoning_tokens, cache_read_input_tokens, cache_creation_input_tokens` | `model_id` + `provider_id` | ms epoch |
| **codex** | `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` | JSONL | `payload.info.total_token_usage.{input_tokens,cached_input_tokens,output_tokens,reasoning_output_tokens,total_tokens}` | `session_meta.payload.model`（及 turn_context） | ISO8601 UTC |
| **workbuddy** | `~/.workbuddy/projects/*/&lt;session&gt;.jsonl` | JSONL | 行内 `usage.{inputTokens,outputTokens}` + `inputTokensDetails[0].cached_tokens`（只认 `"type":"function_call"` 的行，message 行是轮末累计值会重复计） | `providerData.model` | ms epoch |
| **claude code** | `~/.claude/projects/*/*.jsonl` | JSONL | assistant 行 `message.usage.{input_tokens,output_tokens,cache_read_input_tokens,cache_creation_input_tokens,...}`（magpie 同款路径） | `message.model` | ISO8601 |
| **gemini cli** | `~/.gemini/tmp/*/chats/session-*.json` | JSON | `messages[].tokens.{input,output,cached,thoughts}`（magpie 同款路径） | `messages[].model` | 文件内 ISO |
| **qoder cn** | `~/.qoder-cn/logs/sessions/**/*.jsonl` | JSONL | `model.response.completed` 行有 `input_tokens/output_tokens/...`，**实测本机全部为 0**（官方未下发）→ 显示为 0，不做估算 | `model` | ISO8601 带时区 |
| **kimi code** | `~/.kimi/`（magpie：`config.toml`；未发现会话用量库） | — | 本机未安装/无数据 | — | — |
| **pi** | `~/.pi/agent/sessions/…`（magpie 同款路径） | JSONL v3 | 待补 | 待补 | — |

补充事实：
- 本机 `.claude`、`.gemini` 目录存在但**没有会话数据**（只有配置）；`.kimi`、`.pi` 不存在。
  这些源做成"有数据就统计、没数据显示未检测到"，与 magpie 的做法一致。
- 若用户装了 magpie 且开了网关，magpie 自己的 `~/.config/magpie/usage.jsonl`
  也记录了各 agent（agent 字段）的调用——但那是"经过 magpie 代理"的口径，
  与各客户端本地账本口径不同，**不并入**，避免双计。
- workbuddy 的 `session_usage` 表只有 credit 余额（按模型 hash），无 token 明细 → 不用。

## 二、架构设计

```
Core/Abstractions
  ITokenStatsProvider            （不变）
  IAgentTokenSource              （新）— 每个 agent 一个数据源
      string AgentId
      string DisplayName
      bool IsAvailable
      IEnumerable<AgentUsageEntry> ReadEntries(DateOnly fromDay, CancellationToken)

Core/Entities
  AgentUsageEntry                （新）— 一次模型调用的最小记录
      DateTimeOffset Timestamp
      string ModelId / ProviderId
      TokenUsage Usage

Infrastructure/Token/Agents     （新）
  OpencodeAgentSource            复用现有 SQL，改为产出 entries（保留按天缓存）
  ZcodeAgentSource               SQLite model_usage
  CodexAgentSource               JSONL（增量读：按文件 mtime + 文件内 offset）
  WorkbuddyAgentSource           JSONL（只取 function_call 行）
  ClaudeAgentSource              JSONL（~/.claude/projects）
  GeminiAgentSource              JSON（~/.gemini/tmp/*/chats）
  QoderAgentSource               JSONL（模型响应行；tokens 全 0 也如实统计）
  KimiAgentSource / PiAgentSource 路径探测，找不到 → IsAvailable=false

  MultiAgentTokenStatsProvider   （新）ITokenStatsProvider 的聚合实现
      - 构造时注册全部 source
      - FetchAsync(days, granularity, agentFilter)
        · agentFilter = null → 全部可用源合并（按 (Timestamp,AgentId,Model) 去重不作跨源合并——
          各源是各自客户端的独立账本，同一调用不会出现在两个源里）
        · agentFilter = "zcode" → 只用该源
      - 每个 source 自己做按天缓存（沿用 OpenCode 的策略）

Settings
  token.agent    （新）看板记住上次选的 agent，重开恢复
```

### UI（两版一致）
- 范围胶囊行下加一行：`客户端: [全部 ▾]`（ComboBox，含"全部 + 检测到的客户端"），
  选中后趋势/汇总/按模型/复制摘要全部按该 agent 重新统计。
- 看板副标题显示当前口径：`数据来自本机 · opencode+zcode+codex（4/8 可用）`。
- "按模型"明细跟随所选 agent。

### 取数口径细节（防重复计）
- codex：`token_count` 事件的 `total_token_usage` 是**会话累计值**，不能逐行求和！
  正确做法：每个会话文件只取**最后一条** `token_count`（total），或者取相邻两条的
  **差值**（last_token_usage 正好是增量，直接用 `last_token_usage` 求和即可）。
  → 实现用 `last_token_usage`。
- workbuddy：只统计 `type=="function_call"` 行的 `usage`（`message` 行是轮末累计，会重复计）。
- zcode：`model_usage` 每行是一次真实模型请求（含 retry 的 attempt 行，`attempt_index`>0
  是重试），按行求和即真实计费口径。
- opencode：现状不变（每条 assistant 消息一条 tokens）。
- claude：assistant 行的 `message.usage` 是该次响应的用量；`cache_creation_input_tokens`
  映射到 CacheWrite。
- gemini：session json 里 `messages[].tokens` 有 `input/output/cached/thoughts`，
  cached→CacheRead、thoughts→Reasoning。

### 复制摘要
在摘要第一行后追加一行 `客户端：全部 / zcode`（`token.copyAgent` 文案键，四语言）。

## 三、风险与限制（写进文档）
- qoder-cn 官方响应 tokens 全 0（本机 251 次响应实测），统计值会偏小——如实显示，不做字符数估算。
- codex rollout JSONL 可能很大，用"文件大小 + offset"增量读，单文件超过 32MB 只读尾部 8MB。
- 各源时间戳单位不同（ms epoch / ISO8601），统一转 DateTimeOffset 再分桶。
- kimi/pi 本机无数据，源注册但 `IsAvailable=false`；下拉框只列可用源，避免误导。
