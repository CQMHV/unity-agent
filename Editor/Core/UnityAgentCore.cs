using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using AjisaiFlow.UnityAgent.Editor.Interfaces;
using AjisaiFlow.UnityAgent.Editor.MCP;
using AjisaiFlow.UnityAgent.Editor.Providers;
using AjisaiFlow.UnityAgent.Editor.Providers.Gemini;
using AjisaiFlow.UnityAgent.SDK;

namespace AjisaiFlow.UnityAgent.Editor
{
    public class UnityAgentCore
    {
        private List<Message> _history = new List<Message>();
        private ILLMProvider _provider;
        
        private bool _isProcessing;
        public bool IsProcessing => _isProcessing;

        /// <summary>HandleResponse から StartCoroutineOwnerless で起動されたコルーチンハンドル。</summary>
        private readonly List<EditorCoroutineHandle> _activeCoroutines = new List<EditorCoroutineHandle>();
        /// <summary>ProcessUserQuery のルートコルーチンハンドル。</summary>
        private EditorCoroutineHandle _rootCoroutine;

        private int _sessionTotalTokens;
        private int _sessionInputTokens;
        private int _sessionOutputTokens;
        private int _lastPromptTokens;
        public int SessionTotalTokens => _sessionTotalTokens;
        public int SessionInputTokens => _sessionInputTokens;
        public int SessionOutputTokens => _sessionOutputTokens;
        public int LastPromptTokens => _lastPromptTokens;

        public int MaxContextTokens { get; set; } = 900000;

        private static readonly System.Text.RegularExpressions.Regex TokenParseRegex =
            new System.Text.RegularExpressions.Regex(@"\[Tokens: (\d+) \(In: (\d+), Out: (\d+)\)\]",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>コアツール名のセット — LLM が常に署名付きで認識すべき基本ツール。</summary>
        private static readonly HashSet<string> BuiltinMCPToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ReadMCPResource", "GetMCPPrompt", "ListMCPResources", "ListMCPPrompts"
        };

        private static readonly HashSet<string> CoreToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Discovery / Meta
            "SearchTools", "ListTools", "AskUser", "SearchSkills", "ReadSkill",
            // Inspection
            "InspectGameObject", "DeepInspectComponent", "ListRenderers",
            "ListChildren", "GetHierarchyTree", "ListRootObjects", "FindGameObject",
            // Basic Operations
            "SetActive", "SetProperty", "CreateGameObject", "SetParent",
            // SceneView
            "CaptureSceneView", "ScanAvatarMeshes", "CaptureMultiAngle", "FocusSceneView",
            // Assets
            "SearchAssets",
        };

        private int _sessionUndoCount = 0;
        public int SessionUndoCount => _sessionUndoCount;

        private readonly List<ChangeRecord> _changeLog = new List<ChangeRecord>();
        /// <summary>現在処理中のユーザーターンの 0 始まり番号。</summary>
        private int _currentTurnIndex = 0;

        /// <summary>
        /// 1ターン (= 1 ProcessUserQuery 呼び出し) が完了した際に発火するイベント。
        /// 通常完了・ツールループ上限・コンテキスト上限・エラー path のいずれでも発火する。
        /// Self-Driving Test Tools (TestRunner) などプログラマティックに会話を駆動する用途向け。
        /// </summary>
        internal event Action<TurnResult> OnTurnComplete;

        /// <summary>1回のユーザーリクエストに対するツール→LLMループ回数。</summary>
        private int _toolLoopCount;
        private const int MaxToolLoops = 30;

        /// <summary>ExecuteToolsAsync が検出した最初のツール呼び出しの終端位置 (text 内)。
        /// HandleResponse で履歴をここまで切り詰め、ハルシネーション部分を除去するために使用。</summary>
        private int _firstToolEndIndex = -1;

        public void Cancel()
        {
            if (_isProcessing)
            {
                AgentLogger.Info(LogTag.Core, $"Cancel requested. Coroutines={_activeCoroutines.Count}");
                _isProcessing = false;
                // Abort in-flight HTTP request
                _provider.Abort();
                // Stop all spawned coroutines (HandleResponse chains)
                foreach (var h in _activeCoroutines)
                    h.Stop();
                _activeCoroutines.Clear();
                // Stop root coroutine
                _rootCoroutine?.Stop();
                _rootCoroutine = null;
                ToolConfirmState.Clear();
                ToolConfirmState.SessionSkipAll = false;
                BatchToolConfirmState.Clear();
                UserChoiceState.Clear();
                ClipboardProviderState.Clear();
                ToolProgress.Clear();
                // Add system message indicating cancellation
                _history.Add(new Message { role = "user", parts = new[] { new Part { text = "System: Operation cancelled by user." } } });
            }
        }

        public void ClearHistory()
        {
            _history.Clear();
            _sessionUndoCount = 0;
            _changeLog.Clear();
            _currentTurnIndex = 0;
            _sessionTotalTokens = 0;
            _sessionInputTokens = 0;
            _sessionOutputTokens = 0;
            _lastPromptTokens = 0;
        }

        /// <summary>
        /// Truncate history to keep only the first keepCount user messages (plus system/model init).
        /// Used for message edit & resend.
        /// </summary>
        public void TruncateHistory(int keepUserMessageCount)
        {
            if (_history.Count == 0) return;

            int userCount = 0;
            int cutIndex = _history.Count;
            for (int i = 0; i < _history.Count; i++)
            {
                if (_history[i].role == "user" && !_history[i].parts[0].text.StartsWith("Tool Outputs:"))
                {
                    userCount++;
                    if (userCount > keepUserMessageCount)
                    {
                        cutIndex = i;
                        break;
                    }
                }
            }

            if (cutIndex < _history.Count)
                _history.RemoveRange(cutIndex, _history.Count - cutIndex);
        }

        /// <summary>
        /// 現在の _history に含まれる実ユーザーメッセージ数を返す。
        /// 判定は TruncateHistory と同一（"Tool Outputs:" 始まりは除外）。
        /// </summary>
        private int CountUserTurns()
        {
            int n = 0;
            for (int i = 0; i < _history.Count; i++)
            {
                if (_history[i].role == "user" &&
                    _history[i].parts != null && _history[i].parts.Length > 0 &&
                    _history[i].parts[0].text != null &&
                    !_history[i].parts[0].text.StartsWith("Tool Outputs:"))
                    n++;
            }
            return n;
        }

        /// <summary>Unity を変更したツール実行を変更ログに記録する。</summary>
        private void RecordChange(string toolName, string result, int undoGroups)
        {
            string summary = result ?? "";
            summary = summary.Replace("\r", " ").Replace("\n", " ").Trim();
            if (summary.Length > 80) summary = summary.Substring(0, 80) + "...";
            _changeLog.Add(new ChangeRecord
            {
                turnIndex = _currentTurnIndex,
                toolName = toolName ?? "",
                summary = summary,
                undoGroups = undoGroups
            });
        }

        public int UndoAll()
        {
            int count = _sessionUndoCount;
            for (int i = 0; i < count; i++)
                Undo.PerformUndo();
            _sessionUndoCount = 0;
            return count;
        }

        /// <summary>
        /// keepUserMessageCount ターン以降の変更レコード一覧を返す（確認ダイアログ表示用）。
        /// </summary>
        public IReadOnlyList<ChangeRecord> GetChangesAfter(int keepUserMessageCount)
        {
            var list = new List<ChangeRecord>();
            foreach (var c in _changeLog)
                if (c.turnIndex >= keepUserMessageCount)
                    list.Add(c);
            return list;
        }

        /// <summary>
        /// keepUserMessageCount ターン以降の Unity 変更を Undo で巻き戻す。
        /// 巻き戻した Undo グループ数を返す。
        /// </summary>
        public int UndoToUserMessage(int keepUserMessageCount)
        {
            int toUndo = 0;
            foreach (var c in _changeLog)
                if (c.turnIndex >= keepUserMessageCount)
                    toUndo += c.undoGroups;

            for (int i = 0; i < toUndo; i++)
                Undo.PerformUndo();

            _sessionUndoCount = Mathf.Max(0, _sessionUndoCount - toUndo);
            _changeLog.RemoveAll(c => c.turnIndex >= keepUserMessageCount);
            return toUndo;
        }

        /// <summary>変更ログを返す（スナップショット保存用）。</summary>
        public IReadOnlyList<ChangeRecord> GetChangeLog() => _changeLog;

        /// <summary>変更ログを復元する（スナップショット復元時のみ使う）。</summary>
        public void RestoreChangeLog(IEnumerable<ChangeRecord> records)
        {
            _changeLog.Clear();
            if (records != null) _changeLog.AddRange(records);
        }

        public UnityAgentCore(ILLMProvider provider)
        {
            _provider = provider;
        }

        /// <summary>
        /// Programmatic factory for TestRunner — creates a fresh UnityAgentCore with
        /// the specified provider/model. Empty providerId / modelId fall back to the
        /// values currently persisted in AgentSettings (SettingsStore).
        /// </summary>
        /// <param name="providerId">
        /// LLMProviderType enum name (e.g. "Gemini", "Claude_API", "OpenAI"). Case-insensitive.
        /// Empty/null => use the active provider stored in <c>UnityAgent_ProviderType</c>.
        /// </param>
        /// <param name="modelId">
        /// Model identifier (e.g. "gemini-2.5-flash"). Empty/null => use the model currently
        /// saved for the resolved provider (which itself defaults to <c>desc.DefaultModel</c>).
        /// </param>
        /// <exception cref="ArgumentException">providerId did not match any LLMProviderType.</exception>
        /// <exception cref="InvalidOperationException">Resolved provider requires an API key but none is configured.</exception>
        internal static UnityAgentCore CreateProgrammaticInstance(string providerId, string modelId)
        {
            // ── Resolve provider type ──
            LLMProviderType providerType;
            if (string.IsNullOrWhiteSpace(providerId))
            {
                providerType = (LLMProviderType)SettingsStore.GetInt("UnityAgent_ProviderType", 0);
            }
            else if (!Enum.TryParse<LLMProviderType>(providerId, true, out providerType))
            {
                throw new ArgumentException(
                    $"Unknown provider '{providerId}'. Valid: {string.Join(",", Enum.GetNames(typeof(LLMProviderType)))}",
                    nameof(providerId));
            }

            // ── Build ProviderConfig from persisted settings, then optionally override model ──
            var configs = ProviderRegistry.LoadAllConfigs();
            if (!configs.TryGetValue(providerType, out var cfg) || cfg == null)
                throw new InvalidOperationException($"No persisted ProviderConfig found for '{providerType}'.");

            if (!string.IsNullOrWhiteSpace(modelId))
                cfg.ModelName = modelId;

            // ── API key validation — only for SettingsKinds that actually require one.
            //    CLI / Clipboard / BrowserBridge / MCPServer / OpenAI-compatible-URL providers
            //    are excluded (mirrors UnityAgentWindow.IsActiveProviderApiKeyMissing). ──
            var desc = ProviderRegistry.Get(providerType);
            bool requiresApiKey;
            switch (desc.SettingsKind)
            {
                case ProviderSettingsKind.OpenAICompatibleApiKey:
                case ProviderSettingsKind.ClaudeApi:
                case ProviderSettingsKind.VertexAI:
                    requiresApiKey = true;
                    break;
                case ProviderSettingsKind.Gemini:
                    // Custom endpoint mode does not require a key
                    requiresApiKey = cfg.GeminiMode != GeminiConnectionMode.Custom;
                    break;
                default:
                    requiresApiKey = false;
                    break;
            }
            if (requiresApiKey && string.IsNullOrWhiteSpace(cfg.ApiKey))
            {
                throw new InvalidOperationException(
                    $"Provider '{providerType}' has no API key configured. " +
                    "Set it in UnityAgent Settings before invoking the TestRunner.");
            }

            // ── Thinking / effort: TestRunner defaults to disabled for determinism. ──
            const bool useThinking = false;
            const int thinkingBudget = 0;
            const int effortLevel = 0;

            var provider = ProviderRegistry.CreateProvider(providerType, cfg, useThinking, thinkingBudget, effortLevel);
            AgentLogger.Info(LogTag.Core,
                $"CreateProgrammaticInstance: provider={providerType}, model={cfg.ModelName}");
            return new UnityAgentCore(provider);
        }

        // ═══════════════════════════════════════════════════════
        //  Snapshot API — persists LLM conversation across domain reload.
        //  Pairs with Editor/Persistence/ChatSessionPersistence.
        // ═══════════════════════════════════════════════════════

        /// <summary>LLM 履歴の参照を返す (snapshot 取得用、変更しないこと)。</summary>
        public IReadOnlyList<Message> GetHistory() => _history;

        /// <summary>LLM 履歴を直接置き換える。snapshot 復元時のみ使う。</summary>
        public void RestoreHistory(List<Message> history)
        {
            _history = history ?? new List<Message>();
        }

        /// <summary>セッショントークン累積を直接設定する。snapshot 復元時のみ使う。</summary>
        public void RestoreSessionStats(int total, int input, int output, int lastPrompt, int undoCount)
        {
            _sessionTotalTokens = total;
            _sessionInputTokens = input;
            _sessionOutputTokens = output;
            _lastPromptTokens = lastPrompt;
            _sessionUndoCount = undoCount;
        }

        /// <summary>
        /// 自動リトライ可能な状態かを判定する。<see cref="_history"/> の末尾を見て:
        /// - 末尾が user → そのまま再発行可能
        /// - 末尾が model/tool → 不完全なツールループの可能性。末尾の不完全 model ターンを
        ///   pop してから再発行する必要がある
        /// </summary>
        public bool CanResume()
        {
            return _history.Count > 0;
        }

        /// <summary>
        /// 末尾の不完全な assistant/tool ターンを巻き戻して直近の user ターンまで戻す。
        /// 自動リトライ前に呼ぶ。
        /// </summary>
        public void RewindToLastUserTurn()
        {
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].role == "user")
                {
                    int removeFrom = i + 1;
                    if (removeFrom < _history.Count)
                        _history.RemoveRange(removeFrom, _history.Count - removeFrom);
                    return;
                }
            }
        }

        /// <summary>
        /// reload 後に進行中だった LLM 呼び出しを再開する。<see cref="ProcessQueryInternal"/> と同じ
        /// パスで _provider.CallLLM を再発行するが、user message は履歴に追加しない (既に履歴にあるため)。
        /// </summary>
        public IEnumerator ResumeAfterReload(
            Action<string, bool> onReplyReceived,
            Action<string> onStatus = null,
            Action<string> onDebugLog = null,
            Action<string> onPartialResponse = null,
            Action<Interfaces.ChatStreamEvent> onStreamEvent = null)
        {
            if (_isProcessing)
            {
                onDebugLog?.Invoke("[UnityAgentCore] ResumeAfterReload: already processing, skipping.");
                yield break;
            }
            if (_history.Count == 0)
            {
                onDebugLog?.Invoke("[UnityAgentCore] ResumeAfterReload: empty history, nothing to resume.");
                yield break;
            }

            RewindToLastUserTurn();

            _isProcessing = true;
            _toolLoopCount = 0;
            ToolConfirmState.SessionSkipAll = false;

            onPartialResponse?.Invoke(null);

            yield return _provider.CallLLM(
                _history,
                response =>
                {
                    if (!_isProcessing) return;
                    var h = EditorCoroutineUtility.StartCoroutineOwnerless(
                        HandleResponse(response, onReplyReceived, onStatus, onDebugLog, onPartialResponse, onStreamEvent));
                    _activeCoroutines.RemoveAll(x => x.Stopped);
                    _activeCoroutines.Add(h);
                },
                error =>
                {
                    _isProcessing = false;
                    onReplyReceived?.Invoke($"Error: {error}", false);
                },
                onStatus,
                onDebugLog,
                onPartialResponse,
                onStreamEvent
            );
        }

        /// <summary>ルートコルーチンハンドルを設定する。Cancel() で停止するため。</summary>
        public void SetRootCoroutine(EditorCoroutineHandle handle) => _rootCoroutine = handle;

        public IEnumerator ProcessUserQuery(string userMessage, Action<string, bool> onReplyReceived, Action<string> onStatus = null, Action<string> onDebugLog = null, Action<string> onPartialResponse = null, Action<Interfaces.ChatStreamEvent> onStreamEvent = null)
        {
            if (_isProcessing)
            {
                // 状態整合: 前回の処理が完了通知なしで残っている（Provider 例外、coroutine 中断、
                // タイムアウト後の未リセット等）と、ユーザーには送信ボタンが見えているのに次の送信が
                // silent yield break で消える「応答なし」バグになる。自動でキャンセルして救済する。
                AgentLogger.Warning(LogTag.Core,
                    "ProcessUserQuery called while still processing — auto-cancelling previous request to recover.");
                onDebugLog?.Invoke("[UnityAgentCore] Auto-cancelling stuck previous request before new send.");
                Cancel();
            }

            _isProcessing = true;
            _toolLoopCount = 0;
            ToolConfirmState.SessionSkipAll = false;

            // ── TurnResult accumulator for OnTurnComplete subscribers (TestRunner 等) ──
            // 完了 (HandleResponse 内 3 箇所 + エラー path) で発火するため、コールバックをラップして
            // クロージャ経由で turnResult を共有する。
            var turnResult = new TurnResult();
            var turnStart = System.Diagnostics.Stopwatch.StartNew();
            int initialInput = _sessionInputTokens;
            int initialOutput = _sessionOutputTokens;
            bool turnFired = false;

            Action<bool, string> fireTurnComplete = (completed, errorOrNull) =>
            {
                if (turnFired) return;
                turnFired = true;
                turnResult.DurationMs = turnStart.ElapsedMilliseconds;
                turnResult.InputTokens = _sessionInputTokens - initialInput;
                turnResult.OutputTokens = _sessionOutputTokens - initialOutput;
                turnResult.Completed = completed;
                if (errorOrNull != null) turnResult.Error = errorOrNull;
                try { OnTurnComplete?.Invoke(turnResult); }
                catch (Exception evtEx) { Debug.LogError("[UnityAgentCore] OnTurnComplete handler threw: " + evtEx); }
            };

            var userReply = onReplyReceived;
            Action<string, bool> wrappedReply = (text, isFinal) =>
            {
                if (isFinal)
                {
                    turnResult.Text = text;
                    fireTurnComplete(true, null);
                }
                userReply?.Invoke(text, isFinal);
            };

            var userStream = onStreamEvent;
            Action<Interfaces.ChatStreamEvent> wrappedStream = (evt) =>
            {
                if (evt.Kind == Interfaces.StreamEventKind.ToolCallHint && !string.IsNullOrEmpty(evt.Chunk))
                {
                    turnResult.ToolCalls.Add(new ToolCallRecord { Name = evt.Chunk });
                }
                userStream?.Invoke(evt);
            };

            // エラー path: provider.CallLLM の error callback は ProcessQueryInternal 内で
            // _isProcessing=false + onReplyReceived(error, false) を呼ぶが、isFinal=false のため
            // wrappedReply は OnTurnComplete を発火しない。そこで完了監視コルーチンを並走させる。
            yield return ProcessQueryInternal(userMessage, wrappedReply, onStatus, onDebugLog, onPartialResponse, wrappedStream);

            // ProcessQueryInternal が yield 終了しても HandleResponse が StartCoroutineOwnerless で
            // 継続している可能性があるため、_isProcessing が false になるまで待ってから error 検出する。
            // Safety guard: 最大 10 分 (= 600 秒 ≈ ~36000 frame at 60fps editor)
            // 異常終了で _isProcessing が false にならないケースを防ぐ
            const int MAX_POLL_FRAMES = 60 * 600;
            int pollCount = 0;
            while (_isProcessing && pollCount++ < MAX_POLL_FRAMES) yield return null;
            if (pollCount >= MAX_POLL_FRAMES && _isProcessing)
            {
                // タイムアウト時の救済: _isProcessing を強制 false にして UI を解放し、
                // ユーザーに最終通知を送る。これをしないと UI が永久に「処理中」表示で stuck する。
                AgentLogger.Error(LogTag.Core,
                    $"ProcessUserQuery polling exceeded {MAX_POLL_FRAMES} frames; forcing _isProcessing=false and notifying UI.");
                _isProcessing = false;
                _provider?.Abort();
                if (!turnFired)
                    onReplyReceived?.Invoke("Error: 応答待ちがタイムアウトしました。通信が切れた可能性があります。再度お試しください。", true);
            }

            // ここまで来て turnFired==false なら、error path (isFinal=false) かキャンセルで終わったケース。
            if (!turnFired)
                fireTurnComplete(false, turnResult.Error ?? "Cancelled or errored without final reply");
        }

        private IEnumerator ProcessQueryInternal(string userMessage, Action<string, bool> onReplyReceived, Action<string> onStatus, Action<string> onDebugLog, Action<string> onPartialResponse, Action<Interfaces.ChatStreamEvent> onStreamEvent = null)
        {
            onDebugLog?.Invoke($"[UnityAgentCore] ProcessQueryInternal: {userMessage}");

            // Initialize MCP servers if needed (before building system prompt)
            onDebugLog?.Invoke($"[MCP] IsInitialized={MCPManager.IsInitialized}, HasEnabledServers={MCPManager.HasEnabledServers}");
            if (!MCPManager.IsInitialized && MCPManager.HasEnabledServers)
            {
                onDebugLog?.Invoke("[MCP] Starting MCP initialization...");
                yield return MCPManager.Initialize();
                onDebugLog?.Invoke($"[MCP] Initialization complete. Tools: {MCPManager.GetAllTools().Count}");
            }
            else if (MCPManager.IsInitialized)
            {
                // ヘルスチェック: プロセス死亡を検知し自動再接続
                yield return MCPManager.EnsureConnected();
            }

            // Initialize history if empty
            if (_history.Count == 0)
            {
                _history.Add(new Message { role = "system", parts = new[] { new Part { text = GetSystemPrompt() } } });
                _history.Add(new Message { role = "model", parts = new[] { new Part { text = "System initialized." } } });
            }

            // Add selection context to user messages (not tool result feedback)
            string messageText = userMessage;
            if (!userMessage.StartsWith("Tool Outputs:"))
            {
                string selectionContext = GetSelectionContext();
                if (!string.IsNullOrEmpty(selectionContext))
                    messageText = $"{userMessage}\n\n{selectionContext}";
            }

            // Add user message to history (with pending image if available)
            var messageParts = new List<Part> { new Part { text = messageText } };
            if (Tools.SceneViewTools.PendingImageBytes != null)
            {
                messageParts.Add(new Part
                {
                    imageBytes = Tools.SceneViewTools.PendingImageBytes,
                    imageMimeType = Tools.SceneViewTools.PendingImageMimeType
                });
                Tools.SceneViewTools.ClearPendingImage();
            }
            _currentTurnIndex = CountUserTurns();
            _history.Add(new Message { role = "user", parts = messageParts.ToArray() });

            // Signal new streaming session start
            onPartialResponse?.Invoke(null);

            yield return _provider.CallLLM(
                _history,
                response =>
                {
                    if (!_isProcessing) return; // Cancelled

                    // Handle response (check for tool calls) — track handle for cancellation
                    var h = EditorCoroutineUtility.StartCoroutineOwnerless(HandleResponse(response, onReplyReceived, onStatus, onDebugLog, onPartialResponse, onStreamEvent));
                    _activeCoroutines.RemoveAll(x => x.Stopped);
                    _activeCoroutines.Add(h);
                },
                error =>
                {
                    _isProcessing = false;
                    onReplyReceived?.Invoke($"Error: {error}", false);
                },
                onStatus,
                onDebugLog,
                onPartialResponse,
                onStreamEvent
            );
        }

        private IEnumerator HandleResponse(string responseText, Action<string, bool> onReplyReceived, Action<string> onStatus, Action<string> onDebugLog, Action<string> onPartialResponse, Action<Interfaces.ChatStreamEvent> onStreamEvent = null)
        {
            if (!_isProcessing) yield break;

            onDebugLog?.Invoke($"[UnityAgentCore] HandleResponse: {responseText}");

            // Parse token usage from response before stripping
            var tokenMatch = TokenParseRegex.Match(responseText);
            if (tokenMatch.Success)
            {
                int total = int.Parse(tokenMatch.Groups[1].Value);
                int prompt = int.Parse(tokenMatch.Groups[2].Value);
                int output = int.Parse(tokenMatch.Groups[3].Value);
                _sessionTotalTokens += total;
                _sessionInputTokens += prompt;
                _sessionOutputTokens += output;
                _lastPromptTokens = prompt;
            }

            // Strip display-only annotations before adding to history
            // so the model doesn't see [Tokens: ...] or <Thinking> wrappers
            string historyText = StripDisplayAnnotations(responseText);
            _history.Add(new Message { role = "model", parts = new[] { new Part { text = historyText } } });

            var toolResults = new List<string>();
            yield return ExecuteToolsAsync(historyText, onStatus, toolResults);
            if (!_isProcessing) yield break;

            // Truncate history entry to remove hallucinated content after the first tool call.
            // When the LLM outputs multiple tool calls, everything after the first is based on
            // fabricated results and must not persist in the conversation history.
            if (toolResults.Count > 0 && _firstToolEndIndex > 0 && _firstToolEndIndex < historyText.Length)
            {
                string truncated = historyText.Substring(0, _firstToolEndIndex);
                if (_history.Count > 0 && _history[_history.Count - 1].role == "model")
                {
                    _history[_history.Count - 1] = new Message
                    {
                        role = "model",
                        parts = new[] { new Part { text = truncated } }
                    };
                    onDebugLog?.Invoke($"[UnityAgentCore] Truncated model history: removed {historyText.Length - _firstToolEndIndex} chars of hallucinated content after first tool call.");
                }
                _firstToolEndIndex = -1;
            }

            if (toolResults.Count > 0)
            {
                // Check for user choice marker and handle waiting
                for (int i = 0; i < toolResults.Count; i++)
                {
                    if (toolResults[i] == "__WAITING_USER_CHOICE__")
                    {
                        onStatus?.Invoke("__CHOICE__");
                        onDebugLog?.Invoke("[UnityAgentCore] Waiting for user choice...");

                        // Wait for user selection
                        while (UserChoiceState.SelectedIndex < 0)
                        {
                            if (!_isProcessing) yield break;
                            yield return null;
                        }

                        // Replace marker with user's selection
                        string selected = UserChoiceState.CustomText
                            ?? UserChoiceState.Options[UserChoiceState.SelectedIndex];
                        toolResults[i] = UserChoiceState.CustomText != null
                            ? $"User responded: \"{selected}\""
                            : $"User selected: \"{selected}\"";
                        UserChoiceState.Clear();
                        onDebugLog?.Invoke($"[UnityAgentCore] User selected: {selected}");
                    }
                }

                // Feed tool results back to LLM
                var sb = new StringBuilder();
                foreach (var result in toolResults)
                {
                    sb.AppendLine(result);
                }
                string combinedResult = sb.ToString();

                onDebugLog?.Invoke($"[UnityAgentCore] Tool results: {combinedResult}. Feeding back to LLM.");
                _toolLoopCount++;
                int maxTokens = MaxContextTokens;
                if (_isProcessing && _toolLoopCount > MaxToolLoops)
                {
                    onDebugLog?.Invoke($"[UnityAgentCore] Tool loop limit ({MaxToolLoops}) reached. Stopping.");
                    onReplyReceived?.Invoke($"ツールループの上限 ({MaxToolLoops}回) に達しました。処理を中断します。", true);
                    _isProcessing = false;
                }
                else if (_isProcessing && (_lastPromptTokens == 0 || _lastPromptTokens < maxTokens))
                {
                    yield return ProcessQueryInternal($"Tool Outputs:\n{combinedResult}", onReplyReceived, onStatus, onDebugLog, onPartialResponse, onStreamEvent);
                }
                else if (_isProcessing)
                {
                    onDebugLog?.Invoke($"[UnityAgentCore] Context token limit ({maxTokens}) reached (current: {_lastPromptTokens}). Stopping.");
                    onReplyReceived?.Invoke($"コンテキストのトークン上限 ({FormatTokenCount(maxTokens)}) に達しました。処理を中断します。", true);
                    _isProcessing = false;
                }
            }
            else
            {
                onDebugLog?.Invoke($"[UnityAgentCore] No tool call detected. Invoking onReplyReceived.");
                // 「思考だけで終わるバグ」対策:
                // historyText は <Thinking> タグや [Tokens: ...] を除去した本文部分。
                // ここが空 = LLM が thinking のみ返した / 完全に空応答だった ケース。
                // responseText だけで判定すると <Thinking>...</Thinking>[Tokens:...] が non-empty として通過し、
                // ExtractThinking 後に text="" の空 bubble が出るだけでユーザーは何が起きたか分からない。
                string finalText;
                if (string.IsNullOrWhiteSpace(historyText))
                {
                    string notice = "（LLMから本文応答がありませんでした。再度お試しください。）";
                    // Thinking 部分は残してユーザーに参考表示できるよう、responseText の末尾に案内文を付ける。
                    finalText = string.IsNullOrEmpty(responseText)
                        ? notice
                        : responseText.TrimEnd() + "\n\n" + notice;
                }
                else
                {
                    finalText = responseText;
                }
                onReplyReceived?.Invoke(finalText, true);
                _isProcessing = false;
            }
        }

        private IEnumerator ExecuteToolsAsync(string text, Action<string> onStatus, List<string> results)
        {
            // Try bracketed format first: [MethodName(arg1, arg2)]
            // The argument-list grammar (inside the inner non-capturing group) accepts in priority order:
            //   1. """...""" — triple-quoted raw multi-line string (no escape processing)
            //      Pattern: """(?:[^"]|"(?!""))*""" — matches anything not containing 3 consecutive quotes
            //   2. [^'"()]* — bare characters
            //   3. '...' — single-quoted string
            //   4. "..." — double-quoted string
            // Triple-quoted MUST come first so the matcher does not consume it as a sequence of empty "" tokens.
            const string ArgsPattern = @"(?:""""""(?:[^""]|""(?!""""))*""""""|[^'""()]*|'[^']*'|""[^""]*"")*";

            var matches = System.Text.RegularExpressions.Regex.Matches(
                text, $@"\[(\w+)\(({ArgsPattern})\)\]",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            if (matches.Count == 0)
            {
                // Fallback: [MCP/server.MethodName(args)] or [prefix.MethodName(args)] format
                // Captures just the final method name after the last dot or slash
                matches = System.Text.RegularExpressions.Regex.Matches(
                    text, $@"\[[\w/]+[./](\w+)\(({ArgsPattern})\)\]",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
            }

            if (matches.Count == 0)
            {
                // Fallback: [Category: MethodName(args)] format (some models add a label prefix)
                matches = System.Text.RegularExpressions.Regex.Matches(
                    text, $@"\[\w+:\s*(\w+)\(({ArgsPattern})\)\]",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
            }

            if (matches.Count == 0)
            {
                // Fallback: match line-only MethodName(args) without brackets
                // (some models omit the square brackets)
                matches = System.Text.RegularExpressions.Regex.Matches(text, @"^(\w+)\((.*)\)\s*$",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
            }

            if (matches.Count == 0)
            {
                // Fallback: backtick-wrapped tool calls — `MethodName(args)` or ```MethodName(args)```
                // (some models wrap tool calls in markdown code formatting)
                matches = System.Text.RegularExpressions.Regex.Matches(text, @"^`+(\w+)\(((?:[^'""()]*|'[^']*'|""[^""]*"")*)\)`+\s*$",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
            }

            if (matches.Count == 0)
            {
                // Fallback: MethodName(args) at start of line followed by non-tool text
                // Handles cases like: FindFaceEmo()を呼び出して... or ListBlendShapesEx("Body")を確認
                // Uses balanced parentheses matching for simple args
                var lenientMatches = System.Text.RegularExpressions.Regex.Matches(text,
                    @"^(\w+)\(([^)]*)\)",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
                // Only accept if at least one match is a known tool name
                var toolNames = new HashSet<string>(
                    GetToolMethods().Select(m => m.Name),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var mn in MCPManager.GetToolNames()) toolNames.Add(mn);
                foreach (var mn in BuiltinMCPToolNames) toolNames.Add(mn);
                var validMatches = new List<System.Text.RegularExpressions.Match>();
                foreach (System.Text.RegularExpressions.Match lm in lenientMatches)
                {
                    if (lm.Success && toolNames.Contains(lm.Groups[1].Value))
                        validMatches.Add(lm);
                }
                if (validMatches.Count > 0)
                {
                    // Re-run with the same regex — matches already validated above
                    matches = lenientMatches;
                }
            }

            // --- Supplemental: detect tool calls embedded in running text ---
            // Earlier stages may miss unbracketed calls mid-sentence
            // (e.g. "次に AnalyzeGimmickStructure("TK") で確認")
            // This pass always runs and adds non-overlapping, tool-name-validated matches.
            var matchList = new List<System.Text.RegularExpressions.Match>();
            foreach (System.Text.RegularExpressions.Match m in matches)
                if (m.Success) matchList.Add(m);

            {
                var inlineToolNames = new HashSet<string>(
                    GetToolMethods().Select(m => m.Name),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var mn in MCPManager.GetToolNames()) inlineToolNames.Add(mn);
                foreach (var mn in BuiltinMCPToolNames) inlineToolNames.Add(mn);
                var inlineMatches = System.Text.RegularExpressions.Regex.Matches(text,
                    @"(?<!\.)(\w+)\(((?:[^'""()]*|'[^']*'|""[^""]*"")*)\)");
                foreach (System.Text.RegularExpressions.Match im in inlineMatches)
                {
                    if (!im.Success || !inlineToolNames.Contains(im.Groups[1].Value))
                        continue;
                    // Skip if overlapping with an existing match
                    bool overlaps = false;
                    foreach (var existing in matchList)
                    {
                        if (im.Index < existing.Index + existing.Length
                            && im.Index + im.Length > existing.Index)
                        { overlaps = true; break; }
                    }
                    if (!overlaps)
                        matchList.Add(im);
                }
            }

            // --- Enforce single-tool-per-turn ---
            // Only execute the first tool call found in the response.
            // This prevents hallucination cascades where the LLM fabricates tool results
            // and chains additional calls based on imagined data.
            _firstToolEndIndex = -1;
            if (matchList.Count > 0)
            {
                // Sort by position to ensure we pick the first occurrence in text
                matchList.Sort((a, b) => a.Index.CompareTo(b.Index));
                var first = matchList[0];
                _firstToolEndIndex = first.Index + first.Length;
                if (matchList.Count > 1)
                {
                    int ignored = matchList.Count - 1;
                    matchList.RemoveRange(1, ignored);
                    onStatus?.Invoke($"[INFO] {ignored} additional tool call(s) ignored (1-tool-per-turn policy).");
                }
            }

            // --- Batch confirmation pre-scan ---
            HashSet<string> batchApproved = null;
            bool batchUsed = false;
            if (!ToolConfirmState.SessionSkipAll && matchList.Count >= 2)
            {
                var confirmNeeded = new List<BatchToolItem>();
                foreach (var m in matchList)
                {
                    if (!m.Success) continue;
                    string mName = m.Groups[1].Value;
                    var mMethod = GetToolMethods().FirstOrDefault(mt =>
                        string.Equals(mt.Name, mName, StringComparison.OrdinalIgnoreCase));
                    if (mMethod != null && AgentSettings.IsToolConfirmRequired(mMethod.Name))
                    {
                        var mAttr = ToolRegistry.GetAgentToolAttribute(mMethod);
                        confirmNeeded.Add(new BatchToolItem
                        {
                            toolName = mMethod.Name,
                            description = mAttr?.Description ?? mMethod.Name,
                            parameters = m.Groups[2].Value.Trim(),
                            approved = true
                        });
                    }
                }

                if (confirmNeeded.Count >= 2)
                {
                    BatchToolConfirmState.Request(confirmNeeded);
                    onStatus?.Invoke("__BATCH_TOOL_CONFIRM__");

                    while (!BatchToolConfirmState.IsResolved)
                    {
                        if (!_isProcessing) yield break;
                        yield return null;
                    }

                    batchApproved = BatchToolConfirmState.ApprovedTools ?? new HashSet<string>();
                    batchUsed = true;

                    // Check if session skip was set via batch UI
                    if (ToolConfirmState.SessionSkipAll)
                        batchUsed = false; // Skip individual confirmations too

                    BatchToolConfirmState.Clear();
                }
            }

            foreach (var match in matchList)
            {
                if (!_isProcessing) yield break;

                if (match.Success)
                {
                    var methodName = match.Groups[1].Value;
                    var argsString = match.Groups[2].Value;
                    var argsRaw = SplitArguments(argsString);

                    var method = GetToolMethods().FirstOrDefault(m =>
                        string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));
                    if (method != null)
                    {
                        // Invoke tool — split into arg parsing, confirmation, and invocation.
                        // Confirmation and async tools use yield, which C# forbids inside
                        // try-catch (CS1626), so they run outside try-catch blocks.
                        object rawResult = null;
                        int groupBefore = 0;
                        bool invokeOk = false;
                        bool argsParsed = false;

                        var parameterInfos = method.GetParameters();
                        object[] typedArgs = new object[parameterInfos.Length];

                        // --- Phase 1: Arg parsing (in try-catch) ---
                        try
                        {
                            // Log execution attempt
                            string paramsLog = string.Join(", ", argsRaw.Select(a => a.Trim()));
                            onStatus?.Invoke($"Executing Tool: {methodName}({paramsLog})");

                            // Pre-validate argument count
                            int requiredParamCount = parameterInfos.Count(p => !p.HasDefaultValue);
                            if (argsRaw.Length > parameterInfos.Length)
                            {
                                results.Add(GenerateUsageError(method,
                                    $"Error: Too many arguments for {methodName}. Got {argsRaw.Length}, max {parameterInfos.Length} ({requiredParamCount} required, {parameterInfos.Length - requiredParamCount} optional)."));
                                goto NextMatch;
                            }

                            // Initialize all with defaults
                            for (int i = 0; i < parameterInfos.Length; i++)
                            {
                                if (parameterInfos[i].HasDefaultValue)
                                    typedArgs[i] = parameterInfos[i].DefaultValue;
                            }

                            // Unified parser: support mixed positional and named args
                            int positionalIdx = 0;
                            for (int rawIdx = 0; rawIdx < argsRaw.Length; rawIdx++)
                            {
                                string rawArg = argsRaw[rawIdx].Trim();

                                // Check if this is a named argument (name=value)
                                // Skip named-arg detection for quoted literals (e.g. 'Shrink_A=100;Shrink_B=50')
                                bool isQuotedLiteral = (rawArg.Length >= 2)
                                    && ((rawArg[0] == '\'' && rawArg[rawArg.Length - 1] == '\'')
                                     || (rawArg[0] == '"'  && rawArg[rawArg.Length - 1] == '"'));
                                int eqIdx = isQuotedLiteral ? -1 : rawArg.IndexOf('=');
                                if (eqIdx > 0)
                                {
                                    string possibleName = rawArg.Substring(0, eqIdx).Trim().Trim('\'', '"');
                                    // Only treat as named arg if key is a valid identifier (no spaces/special chars)
                                    if (System.Text.RegularExpressions.Regex.IsMatch(possibleName, @"^\w+$"))
                                    {
                                        string valueAfterEq = UnquoteAndUnescape(rawArg.Substring(eqIdx + 1));

                                        int namedIdx = -1;
                                        for (int i = 0; i < parameterInfos.Length; i++)
                                        {
                                            if (string.Equals(parameterInfos[i].Name, possibleName, StringComparison.OrdinalIgnoreCase))
                                            {
                                                namedIdx = i;
                                                break;
                                            }
                                        }

                                        if (namedIdx >= 0)
                                        {
                                            try
                                            {
                                                typedArgs[namedIdx] = Convert.ChangeType(valueAfterEq, parameterInfos[namedIdx].ParameterType);
                                            }
                                            catch (Exception)
                                            {
                                                results.Add(GenerateUsageError(method,
                                                    $"Error: Cannot convert '{valueAfterEq}' to {parameterInfos[namedIdx].ParameterType.Name} for parameter '{parameterInfos[namedIdx].Name}'."));
                                                goto NextMatch;
                                            }
                                            continue;
                                        }

                                        // Named arg key doesn't match any parameter name — return error with valid names
                                        results.Add(GenerateUsageError(method,
                                            $"Error: Unknown parameter '{possibleName}' for {methodName}. Valid parameter names: {string.Join(", ", parameterInfos.Select(p => p.Name))}"));
                                        goto NextMatch;
                                    }
                                }

                                // Positional arg: assign to next open slot
                                while (positionalIdx < parameterInfos.Length && typedArgs[positionalIdx] != null
                                       && !(parameterInfos[positionalIdx].HasDefaultValue && typedArgs[positionalIdx].Equals(parameterInfos[positionalIdx].DefaultValue)))
                                {
                                    positionalIdx++;
                                }

                                if (positionalIdx < parameterInfos.Length)
                                {
                                    string arg = UnquoteAndUnescape(rawArg);
                                    try
                                    {
                                        typedArgs[positionalIdx] = Convert.ChangeType(arg, parameterInfos[positionalIdx].ParameterType);
                                    }
                                    catch (Exception)
                                    {
                                        results.Add(GenerateUsageError(method,
                                            $"Error: Cannot convert '{arg}' to {parameterInfos[positionalIdx].ParameterType.Name} for parameter '{parameterInfos[positionalIdx].Name}'."));
                                        goto NextMatch;
                                    }
                                    positionalIdx++;
                                }
                            }

                            // Check required params (null check + empty string check for required string params)
                            for (int i = 0; i < parameterInfos.Length; i++)
                            {
                                if (typedArgs[i] == null && !parameterInfos[i].HasDefaultValue)
                                {
                                    results.Add(GenerateUsageError(method, $"Error: Missing REQUIRED argument '{parameterInfos[i].Name}'. This parameter must be provided."));
                                    goto NextMatch;
                                }
                                // Reject empty/whitespace-only strings for required string parameters
                                if (!parameterInfos[i].HasDefaultValue
                                    && parameterInfos[i].ParameterType == typeof(string)
                                    && typedArgs[i] is string strVal
                                    && string.IsNullOrWhiteSpace(strVal))
                                {
                                    results.Add(GenerateUsageError(method, $"Error: REQUIRED parameter '{parameterInfos[i].Name}' cannot be empty. Provide a valid value."));
                                    goto NextMatch;
                                }
                            }

                            argsParsed = true;
                        }
                        catch (Exception ex)
                        {
                            string errorMsg = GenerateUsageError(method, $"Error parsing args for {methodName}: {ex.Message}");
                            results.Add(errorMsg);
                            onStatus?.Invoke($"[Tool Error] {ex.Message}");
                        }

                        if (!argsParsed) goto NextMatch;

                        // --- Phase 1.5: Enabled check (block disabled tools) ---
                        {
                            var toolInfo = ToolRegistry.GetAllTools()
                                .FirstOrDefault(t => t.method == method);
                            if (!AgentSettings.IsToolEnabled(method.Name, toolInfo.isExternal))
                            {
                                results.Add($"Error: Tool '{method.Name}' is disabled. Enable it in tool settings.");
                                onStatus?.Invoke($"[Tool Blocked] {method.Name} is disabled");
                                goto NextMatch;
                            }
                        }

                        // --- Phase 2: Per-tool confirmation (outside try-catch for yield) ---
                        if (batchUsed && AgentSettings.IsToolConfirmRequired(method.Name))
                        {
                            // Batch confirmation was used — check pre-approved set
                            if (!batchApproved.Contains(method.Name))
                            {
                                results.Add($"Cancelled by user: {methodName}");
                                onStatus?.Invoke($"[Tool Result] Cancelled by user: {methodName}");
                                goto NextMatch;
                            }
                        }
                        else if (!ToolConfirmState.SessionSkipAll && AgentSettings.IsToolConfirmRequired(method.Name))
                        {
                            var attr = ToolRegistry.GetAgentToolAttribute(method);
                            string desc = attr?.Description ?? method.Name;
                            string paramsStr = string.Join(", ", argsRaw.Select(a => a.Trim()));

                            ToolConfirmState.Request(method.Name, desc, paramsStr);
                            onStatus?.Invoke("__TOOL_CONFIRM__");

                            // Wait for user selection via in-chat buttons
                            while (ToolConfirmState.SelectedIndex < 0)
                            {
                                if (!_isProcessing) yield break;
                                yield return null;
                            }

                            int selection = ToolConfirmState.SelectedIndex;
                            ToolConfirmState.Clear();

                            if (selection == ToolConfirmState.CANCEL)
                            {
                                results.Add($"Cancelled by user: {method.Name}");
                                onStatus?.Invoke($"[Tool Result] Cancelled by user: {method.Name}");
                                goto NextMatch;
                            }
                            if (selection == ToolConfirmState.APPROVE_AND_DISABLE)
                            {
                                AgentSettings.SetToolConfirmRequired(method.Name, false);
                            }
                            else if (selection == ToolConfirmState.APPROVE_ALL_SESSION)
                            {
                                ToolConfirmState.SessionSkipAll = true;
                            }
                        }

                        // --- Phase 3: Invocation (in try-catch) ---
                        try
                        {
                            groupBefore = Undo.GetCurrentGroup();
                            rawResult = method.Invoke(null, typedArgs);
                            invokeOk = true;
                        }
                        catch (System.Reflection.TargetInvocationException tex)
                        {
                            string innerMsg = tex.InnerException?.Message ?? tex.Message;
                            string errorMsg = GenerateUsageError(method, $"Error executing tool {methodName}: {innerMsg}");
                            results.Add(errorMsg);
                            onStatus?.Invoke($"[Tool Error] {innerMsg}");
                        }
                        catch (Exception ex)
                        {
                            string errorMsg = GenerateUsageError(method, $"Error executing tool {methodName}: {ex.Message}");
                            results.Add(errorMsg);
                            onStatus?.Invoke($"[Tool Error] {ex.Message}");
                        }

                        // Process result outside try-catch (yield is not allowed in try-catch)
                        if (invokeOk)
                        {
                            if (rawResult is IEnumerator enumerator)
                            {
                                // Async tool: run as coroutine, collect last string yield as result
                                string asyncResult = null;
                                while (enumerator.MoveNext())
                                {
                                    if (!_isProcessing)
                                    {
                                        (enumerator as IDisposable)?.Dispose();
                                        ToolProgress.Clear();
                                        yield break;
                                    }
                                    if (enumerator.Current is string str)
                                        asyncResult = str;
                                    else
                                        yield return enumerator.Current;
                                }
                                ToolProgress.Clear();
                                int groupAfter = Undo.GetCurrentGroup();
                                int delta = Mathf.Max(0, groupAfter - groupBefore);
                                _sessionUndoCount += delta;
                                string resStr = asyncResult ?? "Error: Async tool completed without result.";
                                if (delta > 0) RecordChange(methodName, resStr, delta);
                                results.Add(resStr);
                                onStatus?.Invoke($"[Tool Result] {resStr}");
                            }
                            else
                            {
                                // Sync tool: use result directly
                                int groupAfter = Undo.GetCurrentGroup();
                                int delta = Mathf.Max(0, groupAfter - groupBefore);
                                _sessionUndoCount += delta;
                                string resStr = rawResult?.ToString() ?? "Success (No return value)";
                                if (delta > 0) RecordChange(methodName, resStr, delta);
                                results.Add(resStr);
                                onStatus?.Invoke($"[Tool Result] {resStr}");
                            }
                        }
                    }
                    else if (BuiltinMCPToolNames.Contains(methodName))
                    {
                        // Built-in MCP resource/prompt tools
                        onStatus?.Invoke($"Executing: {methodName}");
                        string mcpBuiltinResult = null;
                        string mcpBuiltinError = null;

                        if (methodName == "ListMCPResources")
                        {
                            var allRes = MCPManager.GetAllResources();
                            if (allRes.Count == 0)
                                mcpBuiltinResult = "No MCP resources available.";
                            else
                            {
                                var sb = new StringBuilder();
                                foreach (var (srvName, res) in allRes)
                                {
                                    string d = !string.IsNullOrEmpty(res.Description) ? $" — {res.Description}" : "";
                                    sb.AppendLine($"[{srvName}] {res.Name} ({res.Uri}){d}");
                                }
                                mcpBuiltinResult = sb.ToString();
                            }
                        }
                        else if (methodName == "ListMCPPrompts")
                        {
                            var allP = MCPManager.GetAllPrompts();
                            if (allP.Count == 0)
                                mcpBuiltinResult = "No MCP prompts available.";
                            else
                            {
                                var sb = new StringBuilder();
                                foreach (var (srvName, p) in allP)
                                {
                                    string d = !string.IsNullOrEmpty(p.Description) ? $" — {p.Description}" : "";
                                    string argList = "";
                                    if (p.Arguments != null && p.Arguments.Count > 0)
                                        argList = $" args: [{string.Join(", ", p.Arguments.Select(a => a.Required ? a.Name + " [REQUIRED]" : a.Name))}]";
                                    sb.AppendLine($"[{srvName}] {p.Name}{d}{argList}");
                                }
                                mcpBuiltinResult = sb.ToString();
                            }
                        }
                        else if (methodName == "ReadMCPResource")
                        {
                            string uri = argsRaw.Length > 0 ? argsRaw[0].Trim().Trim('\'', '"') : "";
                            if (string.IsNullOrEmpty(uri))
                                mcpBuiltinError = "ReadMCPResource requires a URI argument.";
                            else
                            {
                                var found = MCPManager.FindResource(uri);
                                if (!found.HasValue)
                                    mcpBuiltinError = $"Resource not found: {uri}. Use ListMCPResources() to see available resources.";
                                else
                                {
                                    yield return found.Value.client.ReadResource(uri,
                                        r => mcpBuiltinResult = r,
                                        e => mcpBuiltinError = e);
                                }
                            }
                        }
                        else if (methodName == "GetMCPPrompt")
                        {
                            string promptName = argsRaw.Length > 0 ? argsRaw[0].Trim().Trim('\'', '"') : "";
                            if (string.IsNullOrEmpty(promptName))
                                mcpBuiltinError = "GetMCPPrompt requires a prompt name argument.";
                            else
                            {
                                var found = MCPManager.FindPrompt(promptName);
                                if (!found.HasValue)
                                    mcpBuiltinError = $"Prompt not found: {promptName}";
                                else
                                {
                                    JNode promptArgs = JNode.Obj();
                                    if (argsRaw.Length > 1)
                                    {
                                        string argsJson = argsRaw[1].Trim().Trim('\'', '"');
                                        var parsed = JNode.Parse(argsJson);
                                        if (parsed != null && parsed.Type == JNode.JType.Object)
                                            promptArgs = parsed;
                                        else if (!string.IsNullOrWhiteSpace(argsJson))
                                            mcpBuiltinError = $"Invalid JSON for prompt arguments: {argsJson}";
                                    }
                                    if (mcpBuiltinError == null)
                                    {
                                        yield return found.Value.client.GetPrompt(promptName, promptArgs,
                                            r => mcpBuiltinResult = r,
                                            e => mcpBuiltinError = e);
                                    }
                                }
                            }
                        }

                        if (!_isProcessing) yield break;

                        if (mcpBuiltinError != null)
                            results.Add($"Error: {mcpBuiltinError}");
                        else
                            results.Add(mcpBuiltinResult ?? "(empty)");
                    }
                    else if (MCPManager.HasTool(methodName))
                    {
                        // MCP tool — execute via MCP server
                        var mcpResult = MCPManager.GetTool(methodName);
                        if (mcpResult.HasValue)
                        {
                            var (mcpClient, mcpTool) = mcpResult.Value;
                            onStatus?.Invoke($"Executing MCP Tool: {mcpClient.ServerName}/{methodName}");

                            // Build JSON arguments from positional/named args
                            var jsonArgs = MCPManager.BuildArguments(mcpTool, argsRaw);

                            string mcpResultText = null;
                            string mcpError = null;

                            yield return mcpClient.CallTool(methodName, jsonArgs,
                                r => mcpResultText = r,
                                e => mcpError = e);

                            if (!_isProcessing) yield break;

                            if (mcpError != null)
                            {
                                results.Add($"MCP Error ({mcpClient.ServerName}/{methodName}): {mcpError}");
                                onStatus?.Invoke($"[MCP Error] {mcpError}");
                            }
                            else
                            {
                                string resStr = mcpResultText ?? "(empty)";
                                results.Add(resStr);
                                onStatus?.Invoke($"[MCP Result] {mcpClient.ServerName}/{methodName}");
                            }
                        }
                    }
                    else
                    {
                        var allTools = GetToolMethods();
                        var suggestions = allTools
                            .Where(m => m.Name.IndexOf(methodName, StringComparison.OrdinalIgnoreCase) >= 0
                                     || methodName.IndexOf(m.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(m => m.Name)
                            .Take(5)
                            .ToList();
                        if (suggestions.Count > 0)
                            results.Add($"Error: Tool '{methodName}' not found. Did you mean: {string.Join(", ", suggestions)}? Use SearchTools(\"{methodName}\") to find tools.");
                        else
                            results.Add($"Error: Tool '{methodName}' not found. Use SearchTools(\"{methodName}\") to find available tools.");
                    }
                }
                
                // Yield between tool executions to prevent editor from freezing
                yield return null;
                NextMatch:;
            }
        }

        /// <summary>
        /// Decode a raw argument token produced by SplitArguments into a plain string.
        ///
        /// Supported forms:
        /// - <c>"""...content..."""</c> — raw multi-line string; content is returned verbatim with NO
        ///   escape processing. Use this for file contents that contain quotes, backslashes, or newlines.
        /// - <c>"..."</c> or <c>'...'</c> — conventional quoted string; standard escape sequences
        ///   (\", \', \\, \n, \r, \t, \0, \b, \f, \/) are processed.
        /// - Unquoted tokens are returned as-is (trimmed).
        ///
        /// This is the inverse of what the model emits when it passes string literals inside [Tool(args)] calls.
        /// </summary>
        private static string UnquoteAndUnescape(string rawArg)
        {
            if (rawArg == null) return null;
            string s = rawArg.Trim();
            if (s.Length < 2) return s;

            // Raw triple-quoted string: strip """ from both ends, return inner content verbatim.
            if (s.Length >= 6 && s.StartsWith("\"\"\"", StringComparison.Ordinal)
                && s.EndsWith("\"\"\"", StringComparison.Ordinal))
            {
                return s.Substring(3, s.Length - 6);
            }

            char first = s[0];
            char last = s[s.Length - 1];
            bool isQuoted = (first == '"' && last == '"') || (first == '\'' && last == '\'');
            if (!isQuoted) return s;

            string inner = s.Substring(1, s.Length - 2);
            var sb = new StringBuilder(inner.Length);
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '\\' && i + 1 < inner.Length)
                {
                    char next = inner[i + 1];
                    switch (next)
                    {
                        case '"':  sb.Append('"');  i++; continue;
                        case '\'': sb.Append('\''); i++; continue;
                        case '\\': sb.Append('\\'); i++; continue;
                        case '/':  sb.Append('/');  i++; continue;
                        case 'n':  sb.Append('\n'); i++; continue;
                        case 'r':  sb.Append('\r'); i++; continue;
                        case 't':  sb.Append('\t'); i++; continue;
                        case '0':  sb.Append('\0'); i++; continue;
                        case 'b':  sb.Append('\b'); i++; continue;
                        case 'f':  sb.Append('\f'); i++; continue;
                        default:   sb.Append(c); continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Split tool arguments respecting quoted strings.
        /// e.g. "'path', '0.1,0.2,0.3', 'tint'" → ["'path'", "'0.1,0.2,0.3'", "'tint'"]
        ///
        /// Supported quoting styles:
        /// - 'single' / "double" — conventional quoted strings (honors \-escapes for embedded quotes)
        /// - """triple-quoted""" — raw multi-line string (NO escape processing, any content including
        ///   newlines and embedded quotes is taken verbatim until the next """)
        /// </summary>
        private static string[] SplitArguments(string argsString)
        {
            if (string.IsNullOrWhiteSpace(argsString))
                return new string[0];

            var args = new List<string>();
            var current = new StringBuilder();
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            bool inTripleQuote = false;
            int bracketDepth = 0;
            int braceDepth = 0;

            for (int i = 0; i < argsString.Length; i++)
            {
                char c = argsString[i];

                // Inside a triple-quoted raw string: copy everything verbatim until we meet `"""`.
                // No escape processing, no comma splitting, no bracket tracking.
                if (inTripleQuote)
                {
                    if (c == '"' && i + 2 < argsString.Length && argsString[i + 1] == '"' && argsString[i + 2] == '"')
                    {
                        current.Append("\"\"\"");
                        inTripleQuote = false;
                        i += 2;
                        continue;
                    }
                    current.Append(c);
                    continue;
                }

                // Detect opening triple-quote before any other quote handling.
                if (!inSingleQuote && !inDoubleQuote
                    && c == '"' && i + 2 < argsString.Length
                    && argsString[i + 1] == '"' && argsString[i + 2] == '"')
                {
                    current.Append("\"\"\"");
                    inTripleQuote = true;
                    i += 2;
                    continue;
                }

                // Inside a quoted string, honor backslash escapes so that \" and \' do not
                // prematurely terminate the string and so that \\ does not confuse the
                // quote-state tracker. We copy both the backslash and the escaped char
                // verbatim here — the actual escape processing happens later in
                // UnquoteAndUnescape when the individual argument is decoded.
                if ((inSingleQuote || inDoubleQuote) && c == '\\' && i + 1 < argsString.Length)
                {
                    current.Append(c);
                    current.Append(argsString[i + 1]);
                    i++;
                    continue;
                }
                if (c == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                    current.Append(c);
                }
                else if (c == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    current.Append(c);
                }
                else if (c == '[' && !inSingleQuote && !inDoubleQuote)
                {
                    bracketDepth++;
                    current.Append(c);
                }
                else if (c == ']' && !inSingleQuote && !inDoubleQuote)
                {
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    current.Append(c);
                }
                else if (c == '{' && !inSingleQuote && !inDoubleQuote)
                {
                    braceDepth++;
                    current.Append(c);
                }
                else if (c == '}' && !inSingleQuote && !inDoubleQuote)
                {
                    braceDepth = Math.Max(0, braceDepth - 1);
                    current.Append(c);
                }
                else if (c == '#' && !inSingleQuote && !inDoubleQuote && bracketDepth == 0 && braceDepth == 0)
                {
                    // Skip inline comment until end of line
                    while (i < argsString.Length && argsString[i] != '\n')
                        i++;
                }
                else if (c == ',' && !inSingleQuote && !inDoubleQuote && bracketDepth == 0 && braceDepth == 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
                args.Add(current.ToString());

            return args.ToArray();
        }

        private static readonly Dictionary<string, string> ParamHints = new Dictionary<string, string>
        {
            { "ApplyGradientEx.fromColor", "'#RRGGBB'|'R,G,B'|'transparent'" },
            { "ApplyGradientEx.toColor", "'#RRGGBB'|'R,G,B'|'transparent'" },
            { "ApplyGradientEx.blendMode", "screen|overlay|tint|multiply|replace" },
            { "ApplyGradientEx.direction", "top_to_bottom|bottom_to_top|left_to_right|right_to_left" },
            { "AdjustHSV.hueShift", "-180..+180 RELATIVE" },
            { "AdjustHSV.saturationScale", "0=grayscale,1=unchanged,2=vivid" },
            { "AdjustHSV.valueScale", "0=black,1=unchanged,1.5=brighter" },
            { "GenerateTextureWithAI.textureProperty", "'_MainTex'|'_EmissionMap'|'_BumpMap'" },
        };

        private string GenerateUsageError(MethodInfo method, string errorMessage)
        {
            var sb = new StringBuilder();
            sb.AppendLine(errorMessage);

            var parameters = method.GetParameters();
            int requiredCount = parameters.Count(p => !p.HasDefaultValue);
            int optionalCount = parameters.Length - requiredCount;

            sb.Append("Expected usage: [");
            sb.Append(method.Name);
            sb.Append("(");

            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (p.HasDefaultValue)
                {
                    sb.Append($"{p.ParameterType.Name} {p.Name}");
                    if (p.DefaultValue == null) sb.Append(" = null");
                    else if (p.DefaultValue is string) sb.Append($" = \"{p.DefaultValue}\"");
                    else if (p.DefaultValue is bool) sb.Append($" = {p.DefaultValue.ToString().ToLower()}");
                    else sb.Append($" = {p.DefaultValue}");
                }
                else
                {
                    sb.Append($"{p.ParameterType.Name} {p.Name} [REQUIRED]");
                }
                if (ParamHints.TryGetValue($"{method.Name}.{p.Name}", out var hint))
                    sb.Append($" ({hint})");

                if (i < parameters.Length - 1) sb.Append(", ");
            }

            sb.Append(")]");
            sb.AppendLine();
            sb.Append($"Parameters: {requiredCount} REQUIRED, {optionalCount} optional. You MUST provide all REQUIRED parameters.");
            return sb.ToString();
        }

        private string GetSystemPrompt()
        {
            // sb accumulates ONLY the dynamic tools/MCP section, injected into the template as {{TOOLS}}.
            var sb = new StringBuilder();

            // Section 2: Available Tools — Core (signatures) + Specialized (category summary)
            var toolMethods = ToolRegistry.GetEnabledMethods();

            // Core tools: full signatures grouped by category
            var coreTools = toolMethods.Where(m => CoreToolNames.Contains(m.Name)).ToList();
            var coreCategoryOrder = new[] { "Discovery", "Inspect", "Edit", "SceneView", "Assets" };
            var coreByCategory = new Dictionary<string, List<MethodInfo>>();
            foreach (var m in coreTools)
            {
                string cat;
                if (m.Name == "SearchTools" || m.Name == "ListTools" || m.Name == "AskUser"
                    || m.Name == "SearchSkills" || m.Name == "ReadSkill")
                    cat = "Discovery";
                else if (m.Name == "InspectGameObject" || m.Name == "DeepInspectComponent"
                    || m.Name == "ListRenderers" || m.Name == "ListChildren"
                    || m.Name == "GetHierarchyTree" || m.Name == "ListRootObjects"
                    || m.Name == "FindGameObject")
                    cat = "Inspect";
                else if (m.Name == "SetActive" || m.Name == "SetProperty"
                    || m.Name == "CreateGameObject" || m.Name == "SetParent")
                    cat = "Edit";
                else if (m.Name == "CaptureSceneView" || m.Name == "ScanAvatarMeshes"
                    || m.Name == "CaptureMultiAngle" || m.Name == "FocusSceneView")
                    cat = "SceneView";
                else if (m.Name == "SearchAssets")
                    cat = "Assets";
                else
                    cat = "Other";

                if (!coreByCategory.ContainsKey(cat))
                    coreByCategory[cat] = new List<MethodInfo>();
                coreByCategory[cat].Add(m);
            }

            sb.AppendLine("\nCore Tools (always available — use directly):");
            foreach (var cat in coreCategoryOrder)
            {
                if (!coreByCategory.TryGetValue(cat, out var methods)) continue;
                var signatures = methods.Select(m =>
                {
                    var pars = m.GetParameters();
                    var parNames = string.Join(", ", pars.Select(p =>
                    {
                        string typeName = p.ParameterType == typeof(string) ? "string"
                            : p.ParameterType == typeof(int) ? "int"
                            : p.ParameterType == typeof(float) ? "float"
                            : p.ParameterType == typeof(bool) ? "bool"
                            : p.ParameterType.Name;
                        if (p.HasDefaultValue)
                        {
                            string defVal;
                            if (p.DefaultValue == null) defVal = "null";
                            else if (p.DefaultValue is string s) defVal = $"\"{s}\"";
                            else if (p.DefaultValue is bool b) defVal = b ? "true" : "false";
                            else defVal = p.DefaultValue.ToString();
                            return $"{typeName} {p.Name}={defVal}";
                        }
                        return $"{typeName} {p.Name} [REQUIRED]";
                    }));
                    return $"{m.Name}({parNames})";
                });
                sb.AppendLine($"  {cat}: {string.Join(", ", signatures)}");
            }

            // Specialized tools: category name + count only
            var specializedTools = toolMethods.Where(m => !CoreToolNames.Contains(m.Name)).ToList();
            var specializedGrouped = specializedTools
                .GroupBy(m => m.DeclaringType.Name.Replace("Tools", ""))
                .OrderBy(g => g.Key);
            var specializedSummaries = specializedGrouped
                .Select(g => $"{g.Key}({g.Count()})");
            sb.AppendLine($"\nSpecialized Tools ({specializedTools.Count} total — MUST SearchTools(\"keyword\") before use):");
            sb.AppendLine($"  {string.Join(", ", specializedSummaries)}");

            // Section 2b: MCP Tools (from external MCP servers)
            var mcpTools = MCPManager.GetAllTools();
            if (mcpTools.Count > 0)
            {
                sb.AppendLine("\n  --- MCP Tools (external servers) ---");
                foreach (var (serverName, tool) in mcpTools)
                {
                    string paramList = "";
                    if (tool.Params != null && tool.Params.Count > 0)
                    {
                        var paramParts = tool.Params.Select(p =>
                        {
                            string typeStr = !string.IsNullOrEmpty(p.Type) ? p.Type + " " : "";
                            string reqStr = p.Required ? " [REQUIRED]" : "";
                            return $"{typeStr}{p.Name}{reqStr}";
                        });
                        paramList = string.Join(", ", paramParts);
                    }
                    string desc = !string.IsNullOrEmpty(tool.Description)
                        ? $" — {tool.Description}" : "";
                    sb.AppendLine($"  {tool.Name}({paramList}){desc}");

                    // パラメータの説明を出力（required のみ）
                    if (tool.Params != null)
                    {
                        foreach (var p in tool.Params)
                        {
                            if (p.Required && !string.IsNullOrEmpty(p.Description))
                                sb.AppendLine($"    {p.Name}: {p.Description}");
                        }
                    }
                }
                sb.AppendLine("  Call MCP tools using just the tool name, e.g., [get_current_config()] not [MCP/serena.get_current_config()].");

                // MCP Resources (if any)
                var mcpResources = MCPManager.GetAllResources();
                if (mcpResources.Count > 0)
                {
                    sb.AppendLine("\n  --- MCP Resources (available context) ---");
                    foreach (var (srvName, res) in mcpResources)
                    {
                        string resDesc = !string.IsNullOrEmpty(res.Description) ? $" — {res.Description}" : "";
                        sb.AppendLine($"  [{srvName}] {res.Name} ({res.Uri}){resDesc}");
                    }
                    sb.AppendLine("  To read a resource: [ReadMCPResource(\"uri\")]");
                    sb.AppendLine("  To list all resources: [ListMCPResources()]");
                }

                // MCP Prompts (if any)
                var mcpPrompts = MCPManager.GetAllPrompts();
                if (mcpPrompts.Count > 0)
                {
                    sb.AppendLine("\n  --- MCP Prompts (templates) ---");
                    foreach (var (srvName, p) in mcpPrompts)
                    {
                        string pDesc = !string.IsNullOrEmpty(p.Description) ? $" — {p.Description}" : "";
                        string argInfo = "";
                        if (p.Arguments != null && p.Arguments.Count > 0)
                            argInfo = $" args: [{string.Join(", ", p.Arguments.Select(a => a.Required ? a.Name + " [REQUIRED]" : a.Name))}]";
                        sb.AppendLine($"  [{srvName}] {p.Name}{pDesc}{argInfo}");
                    }
                    sb.AppendLine("  To get a prompt: [GetMCPPrompt(\"name\", '{\"arg\": \"value\"}')]");
                    sb.AppendLine("  To list all prompts: [ListMCPPrompts()]");
                }
            }

            // Dynamic language label for {{LANG}}.
            string lang = AgentSettings.UILanguage;
            var langEntry = AgentSettings.SupportedLanguages
                .FirstOrDefault(l => l.code == lang);
            string langLabel = langEntry.label ?? lang;

            // Dynamic skill summaries for {{SKILLS}}.
            string skillSummaries = Tools.SkillTools.GetSkillSummariesForPrompt() ?? "";

            // Load the static prompt text from Editor/Core/system-prompt.md (readable plain text, no C# escaping)
            // and inject the dynamic sections via tokens.
            return LoadSystemPromptTemplate()
                .Replace("{{TOOLS}}", sb.ToString().TrimEnd())
                .Replace("{{LANG}}", $"{langLabel} ({lang})")
                .Replace("{{SKILLS}}", skillSummaries.TrimEnd());
        }

        /// <summary>
        /// 静的システムプロンプト本文 (Editor/Core/system-prompt.md) を読み込む。
        /// ファイルが見つからない場合 (部分インストール等) は最小限のフォールバックを返し、
        /// エージェントが常に基本動作ルールを持つようにする。
        /// </summary>
        private static string LoadSystemPromptTemplate()
        {
            try
            {
                string path = System.IO.Path.Combine(PackagePaths.PackageRoot, "Editor", "Core", "system-prompt.md");
                if (System.IO.File.Exists(path))
                    return System.IO.File.ReadAllText(path);
                Debug.LogWarning($"[UnityAgent] system-prompt.md not found at {path}; using fallback prompt.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UnityAgent] Failed to load system-prompt.md: {e.Message}; using fallback prompt.");
            }
            return FallbackSystemPrompt;
        }

        private const string FallbackSystemPrompt =
            "You are an AI Agent for Unity Editor. Operate it by calling tools with [MethodName(args)].\n\n" +
            "{{TOOLS}}\n\n" +
            "<rules>\n" +
            "- Reply in {{LANG}}. Write a one-line reason, then end the turn with EXACTLY ONE tool call on the last line; stop immediately after it (no trailing text, no second tool).\n" +
            "- Never fabricate a tool's result, paths, or values; plan only from the real result the system returns.\n" +
            "- Do ONLY what the user asked, then summarize and STOP. Inspect the scene yourself (ListRootObjects, InspectGameObject) instead of asking about structure / names / errors.\n" +
            "- For anything beyond Core Tools, run SearchTools(\"keyword\") and ReadSkill(\"skill\") before acting. Before mesh changes call ScanAvatarMeshes; after visual changes call CaptureSceneView and confirm with AskUser.\n" +
            "</rules>\n\n" +
            "<skills>\nUse SearchSkills(keyword) / ReadSkill(name) for procedures.\n{{SKILLS}}\n</skills>\n";

        private static string FormatTokenCount(int tokens)
        {
            if (tokens >= 1000000) return $"{tokens / 1000000f:0.#}M";
            if (tokens >= 1000) return $"{tokens / 1000f:0.#}k";
            return tokens.ToString();
        }

        private static readonly System.Text.RegularExpressions.Regex TokenInfoRegex =
            new System.Text.RegularExpressions.Regex(@"\n*\[Tokens: \d+.*?\]$", System.Text.RegularExpressions.RegexOptions.Singleline);

        private static readonly System.Text.RegularExpressions.Regex ThinkingWrapperRegex =
            new System.Text.RegularExpressions.Regex(@"^<Thinking>\n[\s\S]*?\n</Thinking>\n*", System.Text.RegularExpressions.RegexOptions.None);

        /// <summary>
        /// Strip [Tokens: ...] suffix and Thinking wrapper from response text
        /// so the model history only contains the raw model output.
        /// </summary>
        private static string StripDisplayAnnotations(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = TokenInfoRegex.Replace(text, "");
            text = ThinkingWrapperRegex.Replace(text, "");
            return text.Trim();
        }

        private static string GetHierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            var current = t.parent;
            while (current != null)
            {
                sb.Insert(0, current.name + "/");
                current = current.parent;
            }
            return sb.ToString();
        }

        private string GetSelectionContext()
        {
            var sb = new StringBuilder();

            // ヒエラルキー選択
            var gameObjects = Selection.gameObjects;
            if (gameObjects.Length > 0)
            {
                sb.AppendLine("[Hierarchy Selection]");
                foreach (var go in gameObjects)
                {
                    string path = GetHierarchyPath(go.transform);
                    sb.AppendLine($"- gameObjectName: {path}");
                }
            }

            // プロジェクトアセット選択（ヒエラルキーと重複しないもの）
            var guids = Selection.assetGUIDs;
            if (guids.Length > 0)
            {
                var hierarchyPaths = new HashSet<string>(
                    gameObjects.Select(go => AssetDatabase.GetAssetPath(go))
                               .Where(p => !string.IsNullOrEmpty(p)));

                var assetLines = new List<string>();
                foreach (var guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(assetPath) || hierarchyPaths.Contains(assetPath))
                        continue;
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    string typeName = obj != null ? obj.GetType().Name : "Unknown";
                    assetLines.Add($"- {assetPath} ({typeName})");
                }
                if (assetLines.Count > 0)
                {
                    sb.AppendLine("[Project Selection]");
                    foreach (var line in assetLines)
                        sb.AppendLine(line);
                }
            }

            // アイランド選択状態
            if (IslandSelectionState.IsActive && IslandSelectionState.SelectedIndices.Count > 0)
            {
                string indices = IslandSelectionState.GetIslandIndicesString();
                string goPath = IslandSelectionState.GetGameObjectPath();
                sb.AppendLine("[Island Selection]");
                sb.AppendLine($"- gameObjectName: {goPath}");
                sb.AppendLine($"- selectedIslands: {indices}");
                sb.AppendLine($"- count: {IslandSelectionState.SelectedIndices.Count}");
            }

            return sb.ToString().TrimEnd();
        }

        private List<MethodInfo> GetToolMethods()
        {
            return ToolRegistry.GetAllMethods();
        }
    }

    // Since we are in Editor, we need a way to run Coroutines
    /// <summary>停止可能なエディタコルーチンハンドル。</summary>
    public class EditorCoroutineHandle
    {
        internal EditorApplication.CallbackFunction Callback;
        internal bool Stopped;

        /// <summary>コルーチンを停止する。</summary>
        public void Stop()
        {
            if (Stopped) return;
            Stopped = true;
            if (Callback != null)
                EditorApplication.update -= Callback;
        }
    }

    public static class EditorCoroutineUtility
    {
        public static EditorCoroutineHandle StartCoroutineOwnerless(IEnumerator routine) => StartCoroutine(routine, null);

        public static EditorCoroutineHandle StartCoroutine(IEnumerator routine, object owner)
        {
            var handle = new EditorCoroutineHandle();
            EditorApplication.CallbackFunction callback = null;
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(routine);
            IEnumerator lastCompleted = null;

            callback = () =>
            {
                // Stop check
                if (handle.Stopped)
                {
                    EditorApplication.update -= callback;
                    return;
                }

                // Safety check
                if (stack == null || stack.Count == 0)
                {
                    EditorApplication.update -= callback;
                    return;
                }

                IEnumerator currentRoutine = stack.Peek();

                // 1. Check if current instruction is waitable
                if (currentRoutine.Current != null)
                {
                    if (currentRoutine.Current is AsyncOperation asyncOp && !asyncOp.isDone)
                    {
                        return; // Wait for async op
                    }
                    else if (currentRoutine.Current is IEnumerator inner && inner != lastCompleted)
                    {
                        // New child IEnumerator — push it to stack
                        stack.Push(inner);
                        lastCompleted = null;
                        return; // Next loop will execute inner.MoveNext()
                    }
                    // If inner == lastCompleted, the child already completed.
                    // Fall through to advance the parent past this yield.
                }

                // 2. Step forward
                lastCompleted = null;
                bool hasMore = currentRoutine.MoveNext();

                if (!hasMore)
                {
                    lastCompleted = currentRoutine;
                    stack.Pop();
                    if (stack.Count == 0)
                    {
                        EditorApplication.update -= callback;
                    }
                }
            };

            handle.Callback = callback;
            EditorApplication.update += callback;
            // Run first step immediately to catch synchronous errors or starts
            callback();
            return handle;
        }
    }

    [Serializable]
    public class Message
    {
        public string role;
        public Part[] parts;
    }

    [Serializable]
    public class Part
    {
        public string text;
        [NonSerialized] public byte[] imageBytes;
        [NonSerialized] public string imageMimeType;
    }

    // CS0649: TestRunner 側が後で populate する placeholder フィールドが存在する。
    // 実装が完成するまで warning を抑制。
#pragma warning disable 649
    /// <summary>
    /// Aggregated result of one programmatic chat turn (used by TestRunner / Self-Driving Test Tools).
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
        public List<ConsoleEntry> ConsoleLogs;  // populated by TestRunner if requested
    }

    internal class ToolCallRecord
    {
        public string Name;
        public string ArgsJson;
        public string Result;
        public long DurationMs;
    }

    internal class ConsoleEntry
    {
        public string Level;       // "Log" / "Warning" / "Error" / "Exception" / "Assert"
        public string Message;
        public string StackTrace;
        public string Timestamp;   // "HH:mm:ss"
        public DateTime At;
    }
#pragma warning restore 649

    /// <summary>
    /// エージェントが 1 ツール実行で行った Unity 変更の記録。
    /// 編集・再生成時の部分巻き戻しと、確認ダイアログのリスト表示に使う。
    /// </summary>
    [System.Serializable]
    public class ChangeRecord
    {
        /// <summary>この変更を生んだユーザーメッセージ（ターン）の 0 始まり番号。</summary>
        public int turnIndex;
        /// <summary>実行したツール名。</summary>
        public string toolName;
        /// <summary>変更内容の短い説明（ツール結果を truncate したもの）。</summary>
        public string summary;
        /// <summary>このツールが生成した Unity Undo グループ数。</summary>
        public int undoGroups;
    }
}
