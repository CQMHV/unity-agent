# UnityAgent Self-Driving Test Tools — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** UnityAgent を外部スクリプト/CI から MCP 経由で駆動できる 6 つの低レベルプリミティブ Tool (StartTestSession / SendTestPrompt / GetSessionState / GetConsoleLogs / SwitchModel / DiscardTestSession) を追加する。

**Architecture:** 新規 `TestRunnerTools.cs` (AgentTool エントリ) + `TestRunnerCore.cs` (セッション管理・完了待機・Console フック) を `Editor/Tools/` 配下に追加。既存 `Editor/Core/UnityAgentCore.cs` に internal API (`CreateProgrammaticSession` / `SubmitProgrammaticTurn` / `DiscardSession` / `OnTurnComplete` event / `TurnResult` DTO) を追加。`SendTestPrompt` のみ worker thread で同期ブロック (MRE.Wait) し、AI 処理はメインスレッドの coroutine で並行進行させる。

**Tech Stack:** Unity 2022.3 / C# / Mono / `ManualResetEventSlim` / `Application.logMessageReceivedThreaded` / リフレクションは不要

**Spec:** `docs/superpowers/specs/2026-05-11-unity-agent-self-driving-design.md`

---

## File Structure

### New Files
- `Editor/Tools/TestRunnerCore.cs` (~300 行)
  - `internal static class TestRunnerCore` — セッション辞書、Console フック、完了待機 MRE、JSON 整形
  - `internal class TestSessionContext` — セッション 1 件分の状態（UnityAgentCore インスタンス、ラベル、作成時刻、現在処理中フラグ、最後のエラー、累積結果バッファ）
  - `internal class TurnResult` — DTO (text, toolCalls List, tokens, durationMs)

- `Editor/Tools/TestRunnerTools.cs` (~250 行)
  - `public static class TestRunnerTools` — 6 つの `[AgentTool]` 静的メソッド
  - 各メソッドは TestRunnerCore に薄く委譲

### Modified Files
- `Editor/Core/UnityAgentCore.cs`
  - 新 internal API: `CreateProgrammaticSession` / `SubmitProgrammaticTurn` / `DiscardSession`
  - 新 internal event: `OnTurnComplete`
  - 既存 `ProcessUserQuery` の onStreamEvent / onReplyReceived からデータ吸い上げ

- `Editor/MCP/AgentMCPServer.Invoker.cs`
  - `SendTestPrompt` だけ worker thread で実行 (新 dispatch hook)

- `localization/tools/*.json` × 22 言語 — 6 エントリ追加
- `Editor/ToolInfra/ToolDescriptionsJP.cs` — `// ── Test Runner ──` セクション + 6 説明追加

### Reference (read-only)
- `Editor/Core/UnityAgentCore.cs:260-299` `ProcessUserQuery` シグネチャ確認
- `Editor/UI/InputBar.cs` 既存ユーザー入力パスの参考
- `Editor/MCP/AgentMCPServer.Invoker.cs` 既存 dispatch パターン
- `Editor/Tools/SceneViewTools.cs:23` `SetPendingImage` 参考 (今回は使わない)

---

## Task 1: Codebase Exploration & UnityAgentCore Surface Capture

**Files:**
- Read only

- [ ] **Step 1: Read existing core files**

```bash
# Read these files in this order to build mental model
Read: C:\code\unity\unity-agent\Editor\Core\UnityAgentCore.cs (1740 lines — focus on lines 1-300, 260-400, 1727-1740)
Read: C:\code\unity\unity-agent\Editor\MCP\AgentMCPServer.cs (find the entry point that dispatches MCP calls to tools)
Read: C:\code\unity\unity-agent\Editor\MCP\AgentMCPServer.Invoker.cs (full)
Read: C:\code\unity\unity-agent\Editor\UI\InputBar.cs (lines 1-150, especially OnSendClicked path)
```

Document in your scratchpad:
- How is `UnityAgentCore` instantiated? (In InputBar? UnityAgentWindow? Per-session?)
- Where does session ID live? (Is there a sessionId field on UnityAgentCore? Or is it in a separate session manager?)
- What's the signature of `ProcessUserQuery` and what callbacks does it expose? (Confirmed: `userMessage, onReplyReceived, onStatus, onDebugLog, onPartialResponse, onStreamEvent`)
- Is `ProcessUserQuery` an IEnumerator (coroutine) or async Task?
- How is the LLM provider chosen per session? Is it an `ILLMProvider` interface?
- Is the AI loop running on main thread (EditorCoroutine) or worker thread?

- [ ] **Step 2: Read session management code**

```bash
Glob: C:\code\unity\unity-agent\Editor\**\*Session*.cs
Glob: C:\code\unity\unity-agent\Editor\**\HistoryPanel.cs
Read: any session-related files
```

Document:
- How are sessions persisted? (JSON files in `Library/`? `UserSettings/`?)
- What's the session ID format? (GUID? incremental? Are there any prefix conventions?)
- How is the active session indicated to the UI?

- [ ] **Step 3: Read MCP invoker carefully**

```bash
Read: C:\code\unity\unity-agent\Editor\MCP\AgentMCPServer.Invoker.cs (full)
```

Find:
- The function that turns an MCP request into a static method invocation
- Where main-thread dispatch happens (likely `EditorApplication.delayCall` or `EditorApplication.update`)
- Whether tool methods are blocking or have async support
- The line numbers where you'd add a per-tool "run on worker thread" branch

- [ ] **Step 4: Read AgentTool attribute**

```bash
Read: C:\code\unity\unity-agent\SDK\AgentToolAttribute.cs
```

Confirm: existing properties (`Description, Author, Version, Category, Url, Risk`). Plan to add a new bool property `RunOnWorkerThread = false` if needed.

- [ ] **Step 5: Commit exploration notes**

No commit yet — this is just reading. Move to Task 2.

---

## Task 2: Add `RunOnWorkerThread` to AgentToolAttribute (if needed)

**Goal:** Allow tools to opt out of main-thread dispatch.

**Files:**
- Modify: `SDK/AgentToolAttribute.cs`

- [ ] **Step 1: Read SDK/AgentToolAttribute.cs**

Confirm the current attribute structure.

- [ ] **Step 2: Add property**

Edit `SDK/AgentToolAttribute.cs`:

```csharp
namespace AjisaiFlow.UnityAgent.SDK
{
    public enum ToolRisk { Safe = 0, Caution = 1, Dangerous = 2 }

    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class AgentToolAttribute : System.Attribute
    {
        public string Description { get; }
        public string Author { get; set; }
        public string Version { get; set; }
        public string Category { get; set; }
        public string Url { get; set; }
        public ToolRisk Risk { get; set; } = ToolRisk.Caution;

        // NEW: When true, MCP invoker runs this tool on the worker thread
        // instead of marshalling to main editor thread. Used for tools that
        // synchronously block waiting for editor coroutine completion (would
        // deadlock if run on main thread).
        public bool RunOnWorkerThread { get; set; } = false;

        public AgentToolAttribute(string description) => Description = description;
    }
}
```

- [ ] **Step 3: Build to confirm SDK assembly compiles**

Run: `cd /c/Users/sakuu/ALCOM/Projects/com.ajisaiflow.vrchat.avater && dotnet build "AjisaiFlow.UnityAgent.SDK.csproj" --nologo 2>&1 | tail -3`

Expected: `0 エラー`

- [ ] **Step 4: Commit**

```bash
git add SDK/AgentToolAttribute.cs
git commit -m "feat(sdk): add RunOnWorkerThread option to AgentToolAttribute"
```

---

## Task 3: Wire `RunOnWorkerThread` into AgentMCPServer.Invoker

**Goal:** Make the MCP invoker check the new flag and skip main-thread dispatch for opted-in tools.

**Files:**
- Modify: `Editor/MCP/AgentMCPServer.Invoker.cs`

- [ ] **Step 1: Identify the dispatch site**

Re-read `AgentMCPServer.Invoker.cs`. Find the function that:
- Receives an MCP request with tool name + args
- Looks up the static method via reflection
- Marshals the call to main thread (probably uses `EditorApplication.delayCall` + `ManualResetEventSlim` already)

Document the specific function name + line number where dispatch happens.

- [ ] **Step 2: Add per-tool routing**

Modify the dispatch function. Pseudocode:

```csharp
// Before invoking, check the AgentToolAttribute on the resolved MethodInfo
var toolAttr = method.GetCustomAttribute<AgentToolAttribute>();
bool runOnWorker = toolAttr?.RunOnWorkerThread ?? false;

if (runOnWorker)
{
    // Invoke directly on this (worker) thread; no main-thread dispatch.
    // The tool itself is responsible for marshalling any Unity API calls back to main.
    var result = method.Invoke(null, args);
    return result;
}
else
{
    // Existing main-thread dispatch path
    ...
}
```

Apply the actual edit based on the existing code structure you read in Step 1.

- [ ] **Step 3: Build verify**

Run: `cd /c/Users/sakuu/ALCOM/Projects/com.ajisaiflow.vrchat.avater && dotnet build "AjisaiFlow.UnityAgent.Editor.csproj" --nologo 2>&1 | tail -3`

Expected: `0 エラー`

- [ ] **Step 4: Commit**

```bash
git add Editor/MCP/AgentMCPServer.Invoker.cs
git commit -m "feat(mcp): respect AgentToolAttribute.RunOnWorkerThread for dispatch routing"
```

---

## Task 4: Add `TurnResult` DTO and `OnTurnComplete` event to UnityAgentCore

**Goal:** Expose programmatic-friendly turn result data and a completion signal.

**Files:**
- Modify: `Editor/Core/UnityAgentCore.cs`

- [ ] **Step 1: Add `TurnResult` and supporting DTOs**

Add at the bottom of the namespace block in `UnityAgentCore.cs` (after the existing `Message` and `Part` classes near line 1740):

```csharp
namespace AjisaiFlow.UnityAgent.Editor
{
    /// <summary>
    /// Aggregated result of one programmatic chat turn.
    /// </summary>
    internal class TurnResult
    {
        public string Text;
        public List<ToolCallRecord> ToolCalls = new List<ToolCallRecord>();
        public int InputTokens;
        public int OutputTokens;
        public int CachedTokens;
        public double EstimatedCostUsd;
        public long DurationMs;
        public bool Completed;
        public string Error;
    }

    internal class ToolCallRecord
    {
        public string Name;
        public string ArgsJson;
        public string Result;
        public long DurationMs;
    }
}
```

(Adjust namespace and `using System.Collections.Generic;` import as needed.)

- [ ] **Step 2: Add `OnTurnComplete` event to UnityAgentCore class**

Inside `class UnityAgentCore` (around line 60 after other events/fields):

```csharp
internal event Action<TurnResult> OnTurnComplete;
```

- [ ] **Step 3: Wire event firing in ProcessUserQuery completion**

Find the end of `ProcessUserQuery` (around line 260-1500 — search for the point where it finishes assistant reply). Add a TurnResult accumulator at start of the function and fire `OnTurnComplete?.Invoke(result)` at completion.

Template:

```csharp
public IEnumerator ProcessUserQuery(string userMessage, ...)
{
    var turnResult = new TurnResult();
    var turnStart = System.Diagnostics.Stopwatch.StartNew();
    int initialInput = _sessionInputTokens;
    int initialOutput = _sessionOutputTokens;

    // EXISTING BODY ...
    // Hook into onStreamEvent or onReplyReceived to capture text and tool calls into turnResult

    turnResult.DurationMs = turnStart.ElapsedMilliseconds;
    turnResult.InputTokens = _sessionInputTokens - initialInput;
    turnResult.OutputTokens = _sessionOutputTokens - initialOutput;
    turnResult.Completed = true;
    OnTurnComplete?.Invoke(turnResult);
}
```

The EXISTING BODY needs careful inspection — find where `onReplyReceived` is called for the final assistant text and where `onStreamEvent` reports tool calls. Capture both into `turnResult`.

- [ ] **Step 4: Build verify**

Run: `cd /c/Users/sakuu/ALCOM/Projects/com.ajisaiflow.vrchat.avater && dotnet build "AjisaiFlow.UnityAgent.Editor.csproj" --nologo 2>&1 | tail -3`

Expected: `0 エラー`

- [ ] **Step 5: Commit**

```bash
git add Editor/Core/UnityAgentCore.cs
git commit -m "feat(core): add TurnResult DTO and OnTurnComplete event"
```

---

## Task 5: Add `CreateProgrammaticSession` / `DiscardSession` API

**Goal:** Allow TestRunner to create isolated UnityAgentCore instances and dispose them.

**Files:**
- Modify: `Editor/Core/UnityAgentCore.cs`

- [ ] **Step 1: Investigate provider creation**

Re-read `UnityAgentCore.cs` line 147 (constructor: `public UnityAgentCore(ILLMProvider provider)`). Find code elsewhere that creates `ILLMProvider` instances by provider/model id. Likely in a Settings or Provider factory class.

```bash
Grep: pattern="ILLMProvider|CreateProvider|new.*Provider", glob="Editor/**/*.cs"
```

Find the helper function that maps `(providerId, modelId)` → `ILLMProvider` instance.

- [ ] **Step 2: Add factory wrapper**

If a clean factory exists, use it. If not, add a static helper inside `UnityAgentCore` (or a new `Editor/Tools/TestRunnerCore.cs` later):

```csharp
internal static UnityAgentCore CreateProgrammaticInstance(string providerId, string modelId)
{
    // Look up settings (current global settings if both empty)
    var settings = AgentSettings.Load();   // or whatever the existing settings type is
    string actualProvider = string.IsNullOrEmpty(providerId) ? settings.ActiveProvider : providerId;
    string actualModel = string.IsNullOrEmpty(modelId) ? settings.ActiveModel : modelId;

    var provider = ProviderFactory.Create(actualProvider, actualModel);  // adjust based on actual API
    return new UnityAgentCore(provider);
}
```

(The exact factory name will be determined in Step 1.)

- [ ] **Step 3: Build verify**

Run: `cd /c/Users/sakuu/ALCOM/Projects/com.ajisaiflow.vrchat.avater && dotnet build "AjisaiFlow.UnityAgent.Editor.csproj" --nologo 2>&1 | tail -3`

Expected: `0 エラー`

- [ ] **Step 4: Commit**

```bash
git add Editor/Core/UnityAgentCore.cs
git commit -m "feat(core): add CreateProgrammaticInstance factory helper"
```

---

## Task 6: Create `TestRunnerCore.cs`

**Goal:** Session lifecycle, MRE waiting, Console hook, JSON formatting.

**Files:**
- Create: `Editor/Tools/TestRunnerCore.cs`

- [ ] **Step 1: Write the file**

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using AjisaiFlow.UnityAgent.Editor.Interfaces;

namespace AjisaiFlow.UnityAgent.Editor.Tools
{
    /// <summary>
    /// Test session manager for programmatic UnityAgent control.
    /// Owns a dictionary of TestSessionContext keyed by sess_xxxx id.
    /// </summary>
    internal static class TestRunnerCore
    {
        public const int MAX_CONCURRENT = 4;
        public const string SESSION_PREFIX = "sess_";

        private static readonly Dictionary<string, TestSessionContext> _sessions = new Dictionary<string, TestSessionContext>();
        private static readonly object _sessionsLock = new object();

        // ── Session lifecycle ──
        public static string CreateSession(string label, string providerId, string modelId)
        {
            lock (_sessionsLock)
            {
                if (_sessions.Count >= MAX_CONCURRENT)
                    throw new InvalidOperationException($"Too many concurrent test sessions ({MAX_CONCURRENT} max). Discard some first.");

                string id = SESSION_PREFIX + Guid.NewGuid().ToString("N").Substring(0, 8);
                var ctx = new TestSessionContext
                {
                    SessionId = id,
                    Label = string.IsNullOrEmpty(label) ? "[TEST] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : "[TEST] " + label,
                    ProviderId = providerId,
                    ModelId = modelId,
                    CreatedAt = DateTime.UtcNow,
                };
                ctx.Core = UnityAgentCore.CreateProgrammaticInstance(providerId, modelId);
                _sessions[id] = ctx;
                return id;
            }
        }

        public static TestSessionContext GetSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || !sessionId.StartsWith(SESSION_PREFIX))
                throw new ArgumentException($"Invalid session id format: '{sessionId}' (must start with '{SESSION_PREFIX}')");
            lock (_sessionsLock)
            {
                if (!_sessions.TryGetValue(sessionId, out var ctx))
                    throw new InvalidOperationException($"Test session '{sessionId}' not found or already discarded.");
                return ctx;
            }
        }

        public static void DiscardSession(string sessionId, bool deleteHistoryFile)
        {
            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(sessionId, out var ctx))
                {
                    try { ctx.Core?.Cancel(); } catch { /* ignore */ }
                    _sessions.Remove(sessionId);
                    if (deleteHistoryFile && !string.IsNullOrEmpty(ctx.HistoryFilePath) && System.IO.File.Exists(ctx.HistoryFilePath))
                    {
                        try { System.IO.File.Delete(ctx.HistoryFilePath); } catch { /* best-effort */ }
                    }
                }
            }
        }

        public static int ActiveSessionCount
        {
            get { lock (_sessionsLock) return _sessions.Count; }
        }

        // ── Sync prompt with timeout ──
        public static TurnResult SendPromptBlocking(string sessionId, string prompt, int timeoutSec, bool captureConsoleLogs)
        {
            var ctx = GetSession(sessionId);
            if (ctx.IsProcessing)
                throw new InvalidOperationException($"Session '{sessionId}' is already processing a prompt.");

            var mre = new ManualResetEventSlim(false);
            TurnResult capturedResult = null;
            Action<TurnResult> handler = (r) => { capturedResult = r; mre.Set(); };

            // Console hook
            List<ConsoleEntry> logs = captureConsoleLogs ? new List<ConsoleEntry>() : null;
            Application.LogCallback consoleHandler = null;
            if (captureConsoleLogs)
            {
                consoleHandler = (logString, stackTrace, type) =>
                {
                    lock (logs) logs.Add(new ConsoleEntry { Level = type.ToString(), Message = logString, StackTrace = stackTrace, Timestamp = DateTime.Now.ToString("HH:mm:ss") });
                };
                Application.logMessageReceivedThreaded += consoleHandler;
            }

            try
            {
                ctx.IsProcessing = true;
                ctx.Core.OnTurnComplete += handler;

                // Dispatch ProcessUserQuery as an EditorCoroutine on main thread
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        EditorCoroutineUtility.StartCoroutineOwnerless(
                            ctx.Core.ProcessUserQuery(prompt, null, null, null, null, null));
                    }
                    catch (Exception ex)
                    {
                        // If dispatch itself fails, set MRE with error result
                        capturedResult = new TurnResult { Completed = false, Error = ex.Message };
                        mre.Set();
                    }
                };

                bool signaled = mre.Wait(TimeSpan.FromSeconds(timeoutSec));
                if (!signaled)
                {
                    return new TurnResult { Completed = false, Error = $"Timeout after {timeoutSec}s" };
                }

                if (logs != null) capturedResult.ConsoleLogs = logs;
                return capturedResult;
            }
            finally
            {
                ctx.Core.OnTurnComplete -= handler;
                if (consoleHandler != null) Application.logMessageReceivedThreaded -= consoleHandler;
                ctx.IsProcessing = false;
            }
        }

        // ── Console-only helper for GetConsoleLogs tool ──
        public static List<ConsoleEntry> GetRecentConsoleLogs(int sinceLastSeconds, string minLevel)
        {
            // Maintain a rolling buffer via a singleton hook (initialized lazily)
            EnsureGlobalConsoleHookActive();
            int minLvl = minLevel?.ToLowerInvariant() switch
            {
                "log" => 0, "info" => 0,
                "warning" => 1,
                "error" => 2,
                _ => 1,
            };
            DateTime cutoff = DateTime.Now.AddSeconds(-sinceLastSeconds);
            lock (_globalLogsLock)
            {
                var result = new List<ConsoleEntry>();
                foreach (var e in _globalLogs)
                {
                    if (e.At < cutoff) continue;
                    int lvl = e.Level == "Error" || e.Level == "Exception" || e.Level == "Assert" ? 2
                            : e.Level == "Warning" ? 1 : 0;
                    if (lvl >= minLvl) result.Add(e);
                }
                return result;
            }
        }

        private static readonly List<ConsoleEntry> _globalLogs = new List<ConsoleEntry>();
        private static readonly object _globalLogsLock = new object();
        private static bool _globalHookInstalled = false;
        private const int GLOBAL_LOG_BUFFER_MAX = 1000;

        private static void EnsureGlobalConsoleHookActive()
        {
            if (_globalHookInstalled) return;
            _globalHookInstalled = true;
            Application.logMessageReceivedThreaded += (logString, stackTrace, type) =>
            {
                lock (_globalLogsLock)
                {
                    _globalLogs.Add(new ConsoleEntry
                    {
                        Level = type.ToString(),
                        Message = logString,
                        StackTrace = stackTrace,
                        Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                        At = DateTime.Now,
                    });
                    if (_globalLogs.Count > GLOBAL_LOG_BUFFER_MAX) _globalLogs.RemoveAt(0);
                }
            };
        }

        // ── JSON formatting ──
        public static string FormatTurnResultJson(TurnResult r)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"completed\":").Append(r.Completed ? "true" : "false");
            sb.Append(",\"text\":").Append(JsonEncode(r.Text ?? ""));
            sb.Append(",\"toolCalls\":[");
            for (int i = 0; i < r.ToolCalls.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var tc = r.ToolCalls[i];
                sb.Append("{\"name\":").Append(JsonEncode(tc.Name));
                sb.Append(",\"args\":").Append(string.IsNullOrEmpty(tc.ArgsJson) ? "{}" : tc.ArgsJson);
                sb.Append(",\"result\":").Append(JsonEncode(tc.Result ?? ""));
                sb.Append(",\"durationMs\":").Append(tc.DurationMs).Append("}");
            }
            sb.Append("],\"tokens\":{");
            sb.Append("\"input\":").Append(r.InputTokens);
            sb.Append(",\"output\":").Append(r.OutputTokens);
            sb.Append(",\"cached\":").Append(r.CachedTokens);
            sb.Append(",\"estCostUsd\":").Append(r.EstimatedCostUsd.ToString("F4"));
            sb.Append("}");
            if (r.ConsoleLogs != null && r.ConsoleLogs.Count > 0)
            {
                sb.Append(",\"consoleLogs\":[");
                for (int i = 0; i < r.ConsoleLogs.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var l = r.ConsoleLogs[i];
                    sb.Append("{\"level\":").Append(JsonEncode(l.Level));
                    sb.Append(",\"message\":").Append(JsonEncode(l.Message));
                    sb.Append(",\"timestamp\":").Append(JsonEncode(l.Timestamp));
                    sb.Append("}");
                }
                sb.Append("]");
            }
            sb.Append(",\"durationMs\":").Append(r.DurationMs);
            if (!string.IsNullOrEmpty(r.Error)) sb.Append(",\"error\":").Append(JsonEncode(r.Error));
            sb.Append("}");
            return sb.ToString();
        }

        private static string JsonEncode(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    internal class TestSessionContext
    {
        public string SessionId;
        public string Label;
        public string ProviderId;
        public string ModelId;
        public DateTime CreatedAt;
        public UnityAgentCore Core;
        public bool IsProcessing;
        public string LastError;
        public string HistoryFilePath;
    }

    internal class ConsoleEntry
    {
        public string Level;
        public string Message;
        public string StackTrace;
        public string Timestamp;
        public DateTime At;
    }
}
```

Make sure to add `ConsoleLogs` field to `TurnResult` in `UnityAgentCore.cs` (Task 4 may not have included it):

```csharp
internal class TurnResult
{
    public string Text;
    public List<ToolCallRecord> ToolCalls = new List<ToolCallRecord>();
    public int InputTokens;
    public int OutputTokens;
    public int CachedTokens;
    public double EstimatedCostUsd;
    public long DurationMs;
    public bool Completed;
    public string Error;
    public List<ConsoleEntry> ConsoleLogs;  // NEW
}
```

- [ ] **Step 2: Build verify**

Run: `cd /c/Users/sakuu/ALCOM/Projects/com.ajisaiflow.vrchat.avater && dotnet build "AjisaiFlow.UnityAgent.Editor.csproj" --nologo 2>&1 | tail -3`

Expected: `0 エラー`. If errors, address them by adjusting based on actual `UnityAgentCore` API surface (constructor args, ProcessUserQuery signature, EditorCoroutineUtility location).

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/TestRunnerCore.cs Editor/Core/UnityAgentCore.cs
git commit -m "feat(tools): add TestRunnerCore — session manager + sync prompt + console hook"
```

---

## Task 7: Create `TestRunnerTools.cs` with 6 [AgentTool] entry points

**Files:**
- Create: `Editor/Tools/TestRunnerTools.cs`

- [ ] **Step 1: Write the file**

```csharp
using System;
using System.Text;
using AjisaiFlow.UnityAgent.SDK;

namespace AjisaiFlow.UnityAgent.Editor.Tools
{
    /// <summary>
    /// External-facing AgentTools that allow CI/scripts to drive UnityAgent itself.
    /// All tools that synchronously block (SendTestPrompt) declare RunOnWorkerThread=true
    /// so they don't deadlock the editor main thread.
    /// </summary>
    public static class TestRunnerTools
    {
        [AgentTool("Create a new isolated test chat session for programmatic AI control. " +
            "Provider/model fall back to global settings if empty. " +
            "Returns a sess_xxxxxxxx id to use with SendTestPrompt / SwitchModel / DiscardTestSession. " +
            "Max 4 concurrent test sessions.",
            Author = "ajisaiflow", Category = "TestRunner", Risk = ToolRisk.Caution)]
        public static string StartTestSession(string sessionLabel = "", string providerId = "", string modelId = "")
        {
            try
            {
                string id = TestRunnerCore.CreateSession(sessionLabel, providerId, modelId);
                var ctx = TestRunnerCore.GetSession(id);
                return $"TestSession {id} created (label={ctx.Label}, provider={ctx.ProviderId}, model={ctx.ModelId}). Use SendTestPrompt('{id}', ...) to send messages.";
            }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        [AgentTool("Send a prompt to a test session and synchronously wait for AI completion. " +
            "Returns JSON with text/toolCalls/tokens/consoleLogs/durationMs. " +
            "timeoutSec (default 120) bounds the wait — on timeout, returns partial results with completed=false. " +
            "captureConsoleLogs (default true) collects Unity Console entries during the turn. " +
            "Recursion-protected: only sessions created via StartTestSession can receive prompts.",
            Author = "ajisaiflow", Category = "TestRunner", Risk = ToolRisk.Caution,
            RunOnWorkerThread = true)]
        public static string SendTestPrompt(string sessionId, string prompt, int timeoutSec = 120, bool captureConsoleLogs = true)
        {
            try
            {
                if (string.IsNullOrEmpty(prompt)) return "Error: prompt is empty.";
                var result = TestRunnerCore.SendPromptBlocking(sessionId, prompt, timeoutSec, captureConsoleLogs);
                return TestRunnerCore.FormatTurnResultJson(result);
            }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        [AgentTool("Get the current state of a test session: message counts, processing flag, last error, model, label, age. Read-only.",
            Author = "ajisaiflow", Category = "TestRunner", Risk = ToolRisk.Safe)]
        public static string GetSessionState(string sessionId)
        {
            try
            {
                var ctx = TestRunnerCore.GetSession(sessionId);
                int total = ctx.Core.GetHistory().Count;
                var sb = new StringBuilder();
                sb.Append($"{ctx.SessionId}: messages={total}, processing={ctx.IsProcessing}, ");
                sb.Append($"lastError={(ctx.LastError ?? "null")}, ");
                sb.Append($"provider={ctx.ProviderId}, model={ctx.ModelId}, label='{ctx.Label}', ");
                sb.Append($"age={(DateTime.UtcNow - ctx.CreatedAt).ToString(@"hh\:mm\:ss")}");
                return sb.ToString();
            }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        [AgentTool("Get recent Unity Console log entries (rolling buffer of last ~1000). " +
            "sinceLastSeconds bounds how far back to look (default 60). " +
            "minLevel: 'log' | 'warning' | 'error' (default 'warning'). Read-only.",
            Author = "ajisaiflow", Category = "TestRunner", Risk = ToolRisk.Safe)]
        public static string GetConsoleLogs(int sinceLastSeconds = 60, string minLevel = "warning")
        {
            try
            {
                var entries = TestRunnerCore.GetRecentConsoleLogs(sinceLastSeconds, minLevel);
                var sb = new StringBuilder();
                sb.AppendLine($"Console logs (last {sinceLastSeconds}s, level >= {minLevel}): {entries.Count} entries");
                sb.AppendLine("---");
                foreach (var e in entries)
                {
                    sb.AppendLine($"[{e.Level}][{e.Timestamp}] {e.Message}");
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        [AgentTool("Switch the AI provider/model on an existing test session. Conversation history is preserved. " +
            "Errors if API key is missing.",
            Author = "ajisaiflow", Category = "TestRunner", Risk = ToolRisk.Caution)]
        public static string SwitchModel(string sessionId, string providerId, string modelId)
        {
            try
            {
                var ctx = TestRunnerCore.GetSession(sessionId);
                string oldP = ctx.ProviderId, oldM = ctx.ModelId;
                // Re-initialize the underlying provider on the existing core
                // (assumes UnityAgentCore exposes a SetProvider(ILLMProvider) method or similar — adjust at impl)
                var newCore = UnityAgentCore.CreateProgrammaticInstance(providerId, modelId);
                newCore.RestoreHistory(new System.Collections.Generic.List<UnityAgentCore.Message>(ctx.Core.GetHistory()));
                ctx.Core = newCore;
                ctx.ProviderId = providerId;
                ctx.ModelId = modelId;
                return $"{ctx.SessionId} model changed: {oldP}/{oldM} → {providerId}/{modelId}";
            }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        [AgentTool("Discard a test session and free its slot in MAX_CONCURRENT counter. " +
            "deleteHistoryFile=true also deletes the persisted JSON file (if any).",
            Author = "ajisaiflow", Category = "TestRunner", Risk = ToolRisk.Caution)]
        public static string DiscardTestSession(string sessionId, bool deleteHistoryFile = false)
        {
            try
            {
                var ctx = TestRunnerCore.GetSession(sessionId);
                string path = ctx.HistoryFilePath;
                TestRunnerCore.DiscardSession(sessionId, deleteHistoryFile);
                string fileMsg = deleteHistoryFile && !string.IsNullOrEmpty(path)
                    ? $"history file deleted ({path})"
                    : (string.IsNullOrEmpty(path) ? "no history file" : $"history file kept at {path}");
                return $"{sessionId} discarded ({fileMsg})";
            }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }
    }
}
```

Note: `UnityAgentCore.Message` reference may need adjustment based on actual namespace. Inspect Task 1 notes.

- [ ] **Step 2: Build verify**

Run: `cd /c/Users/sakuu/ALCOM/Projects/com.ajisaiflow.vrchat.avater && dotnet build "AjisaiFlow.UnityAgent.Editor.csproj" --nologo 2>&1 | tail -3`

Expected: `0 エラー`. Likely needs adjustment for namespace/types.

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/TestRunnerTools.cs
git commit -m "feat(tools): add TestRunnerTools — 6 [AgentTool] for self-driving UnityAgent"
```

---

## Task 8: Update ToolDescriptionsJP.cs

**Files:**
- Modify: `Editor/ToolInfra/ToolDescriptionsJP.cs`

- [ ] **Step 1: Add Test Runner section**

Find the `// ── Window Capture (Windows only) ──` section (added in v0.10.3). Add a new section after it:

```csharp
            // ── Test Runner (programmatic UnityAgent control) ──
            { "StartTestSession", "プログラム制御用テストセッションを作成（UI History に [TEST] プレフィックスで表示）" },
            { "SendTestPrompt", "テストセッションに prompt 送信、AI 完了まで同期待機、結果を JSON で返却" },
            { "GetSessionState", "テストセッションの状態取得（メッセージ数 / 処理中フラグ / モデル / 経過）" },
            { "GetConsoleLogs", "Unity Console の最近のログを取得（rolling buffer 最大 1000 件）" },
            { "SwitchModel", "既存テストセッションのプロバイダー/モデルを切替（履歴保持）" },
            { "DiscardTestSession", "テストセッションを破棄して同時実行枠を解放" },
```

- [ ] **Step 2: Commit**

```bash
git add Editor/ToolInfra/ToolDescriptionsJP.cs
git commit -m "feat(localization): add Test Runner section to ToolDescriptionsJP"
```

---

## Task 9: Update 22 localization JSON files

**Files:**
- Modify: `localization/tools/{ar,cs,da,de,es,fr,hu,id,it,ja,ko,nl,pl,pt,ro,sv,th,tr,uk,vi,zh,zh-TW}.json`

- [ ] **Step 1: Read one file to find insertion point**

Read `localization/tools/ja.json` and find the position right after `"CaptureMonitor"` entry. The 6 new entries go there.

- [ ] **Step 2: Prepare ja.json entries**

For ja.json, insert after `"CaptureMonitor": "..."`:

```json
    "StartTestSession": "プログラム制御用のテストセッションを新規作成します。providerId/modelId を空にすると現在のグローバル設定を継承。同時実行は最大 4。返り値は sess_xxxxxxxx 形式の ID で、SendTestPrompt 等で使用します。",
    "SendTestPrompt": "テストセッションに prompt を送信し、AI の応答完了まで同期で待機します。timeoutSec (デフォルト 120) で最大待機秒数。captureConsoleLogs=true で Unity Console ログも収集。返り値は text / toolCalls / tokens / consoleLogs / durationMs を含む JSON。再帰防止のため StartTestSession で発行されたセッションのみ送信可。",
    "GetSessionState": "テストセッションの現在状態（メッセージ数 / 処理中フラグ / 最後のエラー / プロバイダー / モデル / ラベル / 経過時間）を返します。読み取り専用。",
    "GetConsoleLogs": "Unity Console の直近ログ（rolling buffer 最大 1000 件）を取得します。sinceLastSeconds で過去何秒分か指定。minLevel: 'log' | 'warning' | 'error'。読み取り専用。",
    "SwitchModel": "既存テストセッションの AI プロバイダー/モデルを切り替えます。会話履歴は保持。API キー未設定の場合はエラー。",
    "DiscardTestSession": "テストセッションを破棄します。MAX_CONCURRENT カウンタが減り、新規 StartTestSession が可能に。deleteHistoryFile=true で永続化ファイルも削除。",
```

- [ ] **Step 3: Apply to all 22 files in parallel batches**

For each of the 22 language files, insert localized entries after `"CaptureMonitor"`. Translate to each target language matching the existing tone (similar to how WindowCapture was localized in 0.10.3).

For non-major languages, English fallback is acceptable but should at least be present so all 22 files have the keys.

- [ ] **Step 4: Validate JSON**

Run: `cd /c/code/unity/unity-agent && python -c "import json,glob; [json.load(open(f, encoding='utf-8-sig')) for f in glob.glob('localization/tools/*.json')]; print('OK')"`

Expected: `OK`

- [ ] **Step 5: Commit**

```bash
git add localization/tools/*.json
git commit -m "feat(localization): add Test Runner tool descriptions in 22 languages"
```

---

## Task 10: Build & integration testing

**Files:**
- No file changes; verification only

- [ ] **Step 1: Final build verify**

Run: `cd /c/Users/sakuu/ALCOM/Projects/com.ajisaiflow.vrchat.avater && dotnet build "AjisaiFlow.UnityAgent.Editor.csproj" --nologo 2>&1 | tail -3`

Expected: `0 エラー`

- [ ] **Step 2: Wait for Unity reload (75s if testing in live editor)**

Use ScheduleWakeup tool or ask user to confirm reload.

- [ ] **Step 3: MVP test sequence via MCP**

Execute these MCP calls in order, verifying each:

```
1. mcp__unity-agent__ExecuteUnityTool(name="SearchUnityTool", arguments={"query":"test session"})
   → expect: 6 TestRunner tools in results

2. mcp__unity-agent__ExecuteUnityTool(name="DescribeUnityTool", arguments={"name":"StartTestSession"})
   → expect: full description, RunOnWorkerThread for SendTestPrompt only

3. mcp__unity-agent__ExecuteUnityTool(name="StartTestSession", arguments={"sessionLabel":"smoke"})
   → save sessionId from response

4. mcp__unity-agent__ExecuteUnityTool(name="GetSessionState", arguments={"sessionId":"<id>"})
   → expect: messages=0, processing=false

5. mcp__unity-agent__ExecuteUnityTool(name="SendTestPrompt", arguments={"sessionId":"<id>", "prompt":"List monitors please.", "timeoutSec":60})
   → expect: JSON with text + toolCalls[0].name == "ListMonitors"

6. mcp__unity-agent__ExecuteUnityTool(name="SendTestPrompt", arguments={"sessionId":"<id>", "prompt":"Now capture monitor 0.", "timeoutSec":60})
   → expect: JSON with toolCalls containing "CaptureMonitor"

7. mcp__unity-agent__ExecuteUnityTool(name="DiscardTestSession", arguments={"sessionId":"<id>"})
   → expect: success message

8. mcp__unity-agent__ExecuteUnityTool(name="GetSessionState", arguments={"sessionId":"<id>"})
   → expect: error "not found or already discarded"
```

- [ ] **Step 4: Recursion guard test**

```
mcp__unity-agent__ExecuteUnityTool(name="SendTestPrompt", arguments={"sessionId":"not_a_real_session", "prompt":"hi"})
→ expect: error "Invalid session id format"

mcp__unity-agent__ExecuteUnityTool(name="SendTestPrompt", arguments={"sessionId":"sess_deadbeef", "prompt":"hi"})
→ expect: error "not found or already discarded"
```

- [ ] **Step 5: MAX_CONCURRENT test**

Create 4 sessions, then try a 5th:
```
For i in 1..4: StartTestSession()
StartTestSession()  → expect error "Too many concurrent test sessions (4 max)"
DiscardTestSession × 4  → cleanup
```

- [ ] **Step 6: Timeout test**

```
StartTestSession()
SendTestPrompt(sessionId, "explain quantum mechanics in 10 paragraphs", timeoutSec=2)
→ expect: {completed:false, error:"Timeout after 2s"}
DiscardTestSession()
```

- [ ] **Step 7: Console log capture test**

```
StartTestSession()
SendTestPrompt(sessionId, "Run the C# script: Debug.LogWarning(\"hello\");")
→ expect: in JSON, consoleLogs contains an entry with "hello"
```

- [ ] **Step 8: Editor responsiveness test**

While `SendTestPrompt(timeoutSec=60)` is running, manually verify the Unity editor doesn't freeze (mouse hover responses, scene view updates). This validates the worker-thread routing — if SendTestPrompt accidentally runs on main thread, the editor freezes.

- [ ] **Step 9: Commit final state**

If any test revealed bugs and needed code adjustments, commit those:

```bash
git add -A
git commit -m "fix: address issues from integration testing"
```

---

## Self-Review Notes

After implementation:

1. **Spec coverage** — Verify each item in `docs/superpowers/specs/2026-05-11-unity-agent-self-driving-design.md` Section 2 (Public Tools) maps to a Task above. ✅ All 6 tools covered in Tasks 6/7.
2. **Threading model** — Confirmed in Tasks 2/3 (RunOnWorkerThread attribute + invoker routing) and Task 6 (TestRunnerCore.SendPromptBlocking).
3. **Recursion guards** — Implemented in Task 6 (`SESSION_PREFIX` validation, MAX_CONCURRENT check).
4. **Console logs** — Two paths: per-turn capture (in SendPromptBlocking) and global rolling buffer (for GetConsoleLogs tool).
5. **Token tracking** — Captured via `_sessionInputTokens/_sessionOutputTokens` deltas in `OnTurnComplete` event firing (Task 4 Step 3).
6. **Localization** — 22 langs in Task 9, JP descriptions in Task 8.
7. **Open items resolved at impl time**:
   - Whether AgentMCPServer.Invoker has existing worker-thread routing → Task 1/3 decides
   - Where ProviderFactory lives → Task 5 Step 1 finds it
   - Exact namespace for `Message` type → Task 7 Step 2 may adjust
   - Exact `EditorCoroutineUtility` location → Task 6 may adjust

Out of scope (per spec): parallel prompts in 1 session, AI self-call (blocked), Webhook/SSE, advanced assertion DSL, AgentWebServer integration, visual diff, schema validation.
