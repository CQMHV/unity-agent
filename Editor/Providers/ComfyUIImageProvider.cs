using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AjisaiFlow.UnityAgent.Editor;
using AjisaiFlow.UnityAgent.Editor.Interfaces;
using UnityEngine;
using UnityEngine.Networking;

namespace AjisaiFlow.UnityAgent.Editor.Providers
{
    /// <summary>
    /// ComfyUI のローカル HTTP API を使った img2img 画像生成プロバイダー。
    /// シーケンス: POST /upload/image → POST /prompt → GET /history/{id} ポーリング → GET /view。
    /// WebSocket 非依存 (history ポーリングで完了判定)。
    /// 既存 OpenAIImageProvider / GeminiImageProvider の非ブロッキング SendWebRequest /
    /// Abort パターンに準拠 (自前 EditorCoroutineUtility 駆動のため EditorWaitForSeconds は使わない)。
    /// </summary>
    internal sealed class ComfyUIImageProvider : IImageProvider
    {
        public string ProviderName => "ComfyUI";

        readonly string _baseUrl;
        readonly string _workflowJson; // 空なら内蔵 img2img 雛形
        readonly string _ckpt;
        readonly string _negative;
        readonly float _denoise;
        readonly int _steps;
        readonly float _cfg;
        readonly string _sampler;
        readonly string _scheduler;
        readonly bool _seedRandom;   // true なら毎回ランダム
        readonly ulong _seedFixed;   // _seedRandom=false のとき使用 (ComfyUI seed は 0..2^64-1)
        readonly string _clientId;

        UnityWebRequest _activeRequest;
        bool _aborted;

        const int PollTimeoutSec = 300;
        const double PollIntervalSec = 0.75;

        static readonly System.Random _rng = new System.Random();

        public ComfyUIImageProvider(string baseUrl, string workflowJson, string ckpt, string negative,
            float denoise, int steps, float cfg, string sampler, string scheduler, bool seedRandom, ulong seedFixed)
        {
            _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:8188" : baseUrl.TrimEnd('/');
            _workflowJson = workflowJson ?? "";
            _ckpt = string.IsNullOrEmpty(ckpt) ? "v1-5-pruned-emaonly.safetensors" : ckpt;
            _negative = negative ?? "";
            _denoise = Mathf.Clamp01(denoise);
            _steps = Mathf.Clamp(steps, 1, 150);
            _cfg = Mathf.Clamp(cfg, 0f, 30f);
            _sampler = string.IsNullOrEmpty(sampler) ? "euler" : sampler;
            _scheduler = string.IsNullOrEmpty(scheduler) ? "normal" : scheduler;
            _seedRandom = seedRandom;
            _seedFixed = seedFixed;
            _clientId = Guid.NewGuid().ToString("N");
        }

        public void Abort()
        {
            _aborted = true;
            if (_activeRequest != null && !_activeRequest.isDone)
                _activeRequest.Abort();
            _activeRequest = null;
        }

        public IEnumerator GenerateImage(
            string systemPrompt, string userPrompt, byte[] inputImagePng,
            Action<byte[], string> onSuccess, Action<string> onError,
            Action<string> onStatus = null, Action<string> onDebugLog = null)
        {
            _aborted = false;

            if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            {
                onError?.Invoke($"Base URL が不正です: '{_baseUrl}'。http(s)://host:port 形式で指定してください (既定 http://127.0.0.1:8188)。");
                yield break;
            }

            string combinedPrompt = string.IsNullOrEmpty(systemPrompt)
                ? userPrompt
                : systemPrompt + "\n\n" + userPrompt;

            // 入力解像度 (解像度補正 & alpha 再付与に使用)
            int inW = 0, inH = 0;
            {
                var probe = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    if (probe.LoadImage(inputImagePng)) { inW = probe.width; inH = probe.height; }
                }
                finally { UnityEngine.Object.DestroyImmediate(probe); }
            }
            if (inW == 0 || inH == 0)
            {
                onError?.Invoke("入力画像のデコードに失敗しました (PNG)。");
                yield break;
            }

            ulong seed = _seedRandom ? RandomSeed() : _seedFixed;

            onDebugLog?.Invoke($"[IMAGE REQUEST] Provider: ComfyUI, Base: {_baseUrl}, Input: {inW}x{inH}, seed={seed}, denoise={_denoise}, steps={_steps}");

            // ── 1) POST /upload/image ──
            string uploadedName = null;
            {
                string url = _baseUrl + "/upload/image";
                var form = new List<IMultipartFormSection>
                {
                    new MultipartFormFileSection("image", inputImagePng, "input.png", "image/png"),
                    new MultipartFormDataSection("type", "input"),
                    new MultipartFormDataSection("overwrite", "true"),
                };

                using (HttpHelper.AllowInsecureIfNeeded(url))
                using (var req = UnityWebRequest.Post(url, form))
                {
                    _activeRequest = req;
                    req.timeout = 120;

                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        if (_aborted) { req.Abort(); _activeRequest = null; yield break; }
                        yield return null;
                    }
                    _activeRequest = null;
                    if (_aborted) yield break;

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        onError?.Invoke(BuildConnError("画像アップロード", req));
                        yield break;
                    }

                    uploadedName = ExtractJsonString(req.downloadHandler.text, "name");
                    if (string.IsNullOrEmpty(uploadedName))
                    {
                        onError?.Invoke($"アップロード応答に name がありません。Response: {Truncate(req.downloadHandler.text, 300)}");
                        yield break;
                    }
                }
            }

            // ── workflow JSON 構築 (プレースホルダ置換) ──
            string workflow;
            {
                string template = string.IsNullOrEmpty(_workflowJson) ? BuiltinImg2ImgTemplate : _workflowJson;

                // ユーザ workflow は最低 {{IMAGE}} を含むこと
                if (!string.IsNullOrEmpty(_workflowJson) && !template.Contains("{{IMAGE}}"))
                {
                    onError?.Invoke("カスタム workflow に {{IMAGE}} がありません。入力画像を読む LoadImage の image を \"{{IMAGE}}\" にしてください (各プレースホルダは JSON の文字列値の中に置くこと)。");
                    yield break;
                }
                if (!string.IsNullOrEmpty(_workflowJson) && !string.IsNullOrEmpty(combinedPrompt) && !template.Contains("{{POSITIVE}}"))
                    onDebugLog?.Invoke("[ComfyUI] カスタム workflow に {{POSITIVE}} が無いため、入力プロンプトは反映されません。");

                workflow = template
                    .Replace("{{IMAGE}}", JsonEscapeInner(uploadedName))
                    .Replace("{{POSITIVE}}", JsonEscapeInner(combinedPrompt))
                    .Replace("{{NEGATIVE}}", JsonEscapeInner(_negative))
                    .Replace("{{CKPT}}", JsonEscapeInner(_ckpt))
                    .Replace("{{SAMPLER}}", JsonEscapeInner(_sampler))
                    .Replace("{{SCHEDULER}}", JsonEscapeInner(_scheduler))
                    .Replace("{{SEED}}", seed.ToString(CultureInfo.InvariantCulture))
                    .Replace("{{STEPS}}", _steps.ToString(CultureInfo.InvariantCulture))
                    .Replace("{{CFG}}", _cfg.ToString(CultureInfo.InvariantCulture))
                    .Replace("{{DENOISE}}", _denoise.ToString(CultureInfo.InvariantCulture));

                int leftover = workflow.IndexOf("{{", StringComparison.Ordinal);
                if (leftover >= 0)
                {
                    int end = workflow.IndexOf("}}", leftover, StringComparison.Ordinal);
                    string token = end > leftover ? workflow.Substring(leftover, end - leftover + 2) : workflow.Substring(leftover, Math.Min(20, workflow.Length - leftover));
                    onError?.Invoke($"workflow に未対応のプレースホルダが残っています: {token}");
                    yield break;
                }
            }

            // ── 2) POST /prompt ──
            string promptId = null;
            {
                string url = _baseUrl + "/prompt";
                string body = "{\"prompt\":" + workflow + ",\"client_id\":\"" + _clientId + "\"}";
                byte[] bodyRaw = Encoding.UTF8.GetBytes(body);

                using (HttpHelper.AllowInsecureIfNeeded(url))
                using (var req = new UnityWebRequest(url, "POST"))
                {
                    _activeRequest = req;
                    req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.SetRequestHeader("Content-Type", "application/json");
                    req.timeout = 120;

                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        if (_aborted) { req.Abort(); _activeRequest = null; yield break; }
                        yield return null;
                    }
                    _activeRequest = null;
                    if (_aborted) yield break;

                    string resp = req.downloadHandler?.text ?? "";
                    if (req.result != UnityWebRequest.Result.Success || HasNodeErrors(resp))
                    {
                        onError?.Invoke(BuildPromptError(resp, req, !string.IsNullOrEmpty(_workflowJson)));
                        yield break;
                    }

                    promptId = ExtractJsonString(resp, "prompt_id");
                    if (string.IsNullOrEmpty(promptId))
                    {
                        onError?.Invoke($"/prompt 応答に prompt_id がありません。Response: {Truncate(resp, 300)}");
                        yield break;
                    }
                }
            }

            // ── 3) GET /history/{prompt_id} ポーリング ──
            string filename = null, subfolder = "", viewType = "output";
            {
                double startTime = UnityEditor.EditorApplication.timeSinceStartup;
                bool found = false;

                while (!found)
                {
                    if (_aborted) yield break;
                    if (UnityEditor.EditorApplication.timeSinceStartup - startTime > PollTimeoutSec)
                    {
                        onError?.Invoke($"生成がタイムアウトしました ({PollTimeoutSec}s)。ComfyUI の処理状況を確認してください。");
                        yield break;
                    }

                    string url = _baseUrl + "/history/" + promptId;
                    using (HttpHelper.AllowInsecureIfNeeded(url))
                    using (var req = UnityWebRequest.Get(url))
                    {
                        _activeRequest = req;
                        req.timeout = 30;

                        var op = req.SendWebRequest();
                        while (!op.isDone)
                        {
                            if (_aborted) { req.Abort(); _activeRequest = null; yield break; }
                            yield return null;
                        }
                        _activeRequest = null;
                        if (_aborted) yield break;

                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            string h = req.downloadHandler.text ?? "";
                            // history は {"<prompt_id値>":{...}}。entry(outputs/status)出現=terminal、空 "{}" は実行中。
                            if (HasHistoryEntry(h))
                            {
                                // 出力画像があれば成功 (status_str 非依存: status ブロックを持たない旧/フォーク ComfyUI も救済)
                                if (TrySelectOutputImage(h, out filename, out subfolder, out viewType))
                                {
                                    found = true;
                                }
                                else
                                {
                                    // entry はあるが出力画像なし = error / interrupted / cancelled 等で終端
                                    string status = ExtractJsonString(h, "status_str");
                                    onError?.Invoke($"ComfyUI 生成が出力画像なしで終了しました (status={status ?? "null"})。\n{ExtractMessages(h)}");
                                    yield break;
                                }
                            }
                        }
                    }

                    if (found) break;

                    int elapsed = (int)(UnityEditor.EditorApplication.timeSinceStartup - startTime);
                    onStatus?.Invoke($"ComfyUI 生成中… {elapsed}s");

                    double waitStart = UnityEditor.EditorApplication.timeSinceStartup;
                    while (UnityEditor.EditorApplication.timeSinceStartup - waitStart < PollIntervalSec)
                    {
                        if (_aborted) yield break;
                        yield return null;
                    }
                }
            }

            // ── 4) GET /view ──
            byte[] outPng = null;
            {
                string url = _baseUrl + "/view?filename=" + UnityWebRequest.EscapeURL(filename)
                    + "&subfolder=" + UnityWebRequest.EscapeURL(subfolder)
                    + "&type=" + UnityWebRequest.EscapeURL(viewType);

                using (HttpHelper.AllowInsecureIfNeeded(url))
                using (var req = UnityWebRequest.Get(url))
                {
                    _activeRequest = req;
                    req.timeout = 120;

                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        if (_aborted) { req.Abort(); _activeRequest = null; yield break; }
                        yield return null;
                    }
                    _activeRequest = null;
                    if (_aborted) yield break;

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        onError?.Invoke(BuildConnError("生成画像取得", req));
                        yield break;
                    }
                    outPng = req.downloadHandler.data;
                }
            }

            // ── 5) alpha 再付与 + 入力解像度へ補正 ──
            string ppError;
            byte[] finalPng = PostProcess(outPng, inputImagePng, inW, inH, out ppError);
            if (finalPng == null)
            {
                onError?.Invoke(ppError ?? "生成画像の後処理に失敗しました。");
                yield break;
            }

            onSuccess?.Invoke(finalPng, $"ComfyUI seed={seed}, steps={_steps}, cfg={_cfg}, denoise={_denoise}");
        }

        // ─── 後処理: 解像度補正 + alpha 再付与 ───

        /// <summary>
        /// ComfyUI 出力 (RGB・透過なし) を入力解像度に合わせ、入力 PNG の alpha を再付与して PNG を返す。
        /// SD VAE は RGB 3ch のみで透過が失われるため、TextureGenerationTools の alpha 依存処理向けに復元する。
        /// </summary>
        static byte[] PostProcess(byte[] outPng, byte[] inputPng, int inW, int inH, out string error)
        {
            error = null;
            Texture2D outTex = null, inTex = null, resized = null;
            try
            {
                outTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!outTex.LoadImage(outPng))
                {
                    error = DescribeDecodeError(outPng);
                    return null;
                }

                // 入力解像度へ補正 (LoadImage→VAEEncode で原則一致するが念のため)
                if (outTex.width != inW || outTex.height != inH)
                {
                    resized = BlitResize(outTex, inW, inH);
                    UnityEngine.Object.DestroyImmediate(outTex);
                    outTex = resized;
                    resized = null;
                }

                // 入力の alpha を再付与 (同寸前提)
                inTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (inTex.LoadImage(inputPng) && inTex.width == outTex.width && inTex.height == outTex.height)
                {
                    var oc = outTex.GetPixels32();
                    var ic = inTex.GetPixels32();
                    for (int i = 0; i < oc.Length && i < ic.Length; i++)
                        oc[i].a = ic[i].a;
                    outTex.SetPixels32(oc);
                    outTex.Apply(false);
                }

                byte[] png = outTex.EncodeToPNG();
                if (png == null || png.Length == 0)
                {
                    error = "生成画像の PNG エンコードに失敗しました。";
                    return null;
                }
                return png;
            }
            catch (Exception ex)
            {
                error = $"後処理エラー: {ex.Message}";
                return null;
            }
            finally
            {
                if (outTex != null) UnityEngine.Object.DestroyImmediate(outTex);
                if (inTex != null) UnityEngine.Object.DestroyImmediate(inTex);
                if (resized != null) UnityEngine.Object.DestroyImmediate(resized);
            }
        }

        /// <summary>読み取り可能 Texture2D を指定サイズへ Blit リサイズ (alpha 保持)。</summary>
        static Texture2D BlitResize(Texture2D src, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply(false);
                return tex;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static ulong RandomSeed()
        {
            var b = new byte[8];
            _rng.NextBytes(b);
            return BitConverter.ToUInt64(b, 0); // ComfyUI seed は 0..2^64-1
        }

        // ─── エラー整形 ───

        static string BuildConnError(string stage, UnityWebRequest req)
        {
            if (req.result == UnityWebRequest.Result.ConnectionError)
                return "ComfyUI に接続できません。ComfyUI が起動しているか、Base URL/ポート (既定 http://127.0.0.1:8188) を確認してください。";
            string body = req.downloadHandler?.text ?? "";
            return $"{stage}に失敗しました: {req.error} (Code {req.responseCode})\n{Truncate(body, 300)}";
        }

        static string BuildPromptError(string resp, UnityWebRequest req, bool customWorkflow)
        {
            if (req.result == UnityWebRequest.Result.ConnectionError)
                return "ComfyUI に接続できません。ComfyUI が起動しているか、Base URL/ポートを確認してください。";
            string hint = "";
            if (resp.IndexOf("ckpt_name", StringComparison.OrdinalIgnoreCase) >= 0
                || resp.IndexOf("CheckpointLoader", StringComparison.OrdinalIgnoreCase) >= 0)
                hint += "\nチェックポイント名 (ckpt) が ComfyUI の models/checkpoints/ に存在するか確認してください。";
            if (customWorkflow)
                hint += "\nカスタム workflow を使用中です。ComfyUI の Save (API Format) で出力した有効な JSON か確認してください。";
            return $"ComfyUI への投入に失敗しました (Code {req.responseCode})。{hint}\n{Truncate(resp, 500)}";
        }

        /// <summary>デコード不可バイト列の形式を推定して actionable なメッセージを返す。</summary>
        static string DescribeDecodeError(byte[] b)
        {
            if (b == null || b.Length < 4) return "生成画像が空でした。";
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "生成画像 (PNG) のデコードに失敗しました。";
            if (b[0] == 0xFF && b[1] == 0xD8) return "生成画像 (JPEG) のデコードに失敗しました。";
            if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
                && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
                return "出力が WebP 形式です。出力ノードを PNG を出す SaveImage にしてください (WebP/アニメ/動画は非対応)。";
            if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
                return "出力が GIF 形式です。出力ノードを PNG を出す SaveImage にしてください。";
            return "生成画像をデコードできません (PNG/JPG のみ対応。WebP/動画/アニメ出力は非対応)。";
        }

        // ─── JSON ユーティリティ ───

        /// <summary>JSON 文字列値の "中身" (両端 " 無し) としてエスケープ。制御文字は \u00XX。</summary>
        static string JsonEscapeInner(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '{': sb.Append("\\u007b"); break; // {{ }} がプロンプト残存チェックを誤爆しないよう常時エスケープ
                    case '}': sb.Append("\\u007d"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>key の文字列値を取得 (見つからない/空は null)。fromIndex 以降を探索。</summary>
        static string ExtractJsonString(string json, string key, int fromIndex = 0)
        {
            int start = FindJsonStringValue(json, key, fromIndex);
            if (start < 0) return null;
            return ReadJsonStringFrom(json, start);
        }

        /// <summary>空文字列も成功扱いで取得 (subfolder 用)。見つからなければ null。</summary>
        static string ExtractJsonStringAllowEmpty(string json, string key, int fromIndex)
        {
            int start = FindJsonStringValue(json, key, fromIndex);
            if (start < 0) return null;
            int end = json.IndexOf('"', start);
            if (end < start) return null;
            return json.Substring(start, end - start); // end==start なら ""
        }

        /// <summary>key を [fromIndex, maxIndex] の範囲に限定して取得 (オブジェクト境界跨ぎ防止)。</summary>
        static string ExtractJsonStringBounded(string json, string key, int fromIndex, int maxIndex)
        {
            int start = FindJsonStringValue(json, key, fromIndex);
            if (start < 0 || (maxIndex >= 0 && start > maxIndex)) return null;
            return ReadJsonStringFrom(json, start);
        }

        /// <summary>空文字許容 + 範囲限定版。</summary>
        static string ExtractJsonStringAllowEmptyBounded(string json, string key, int fromIndex, int maxIndex)
        {
            int start = FindJsonStringValue(json, key, fromIndex);
            if (start < 0 || (maxIndex >= 0 && start > maxIndex)) return null;
            int end = json.IndexOf('"', start);
            if (end < start) return null;
            return json.Substring(start, end - start);
        }

        /// <summary>値開始位置 (開いた " の次) から閉じ " までを返す。非空前提。</summary>
        static string ReadJsonStringFrom(string json, int valueStart)
        {
            int end = json.IndexOf('"', valueStart);
            if (end <= valueStart) return null;
            return json.Substring(valueStart, end - valueStart);
        }

        /// <summary>history の status の messages を抜き出す (簡易・表示用)。</summary>
        static string ExtractMessages(string json)
        {
            int idx = json.IndexOf("\"exception_message\"", StringComparison.Ordinal);
            if (idx < 0) idx = json.IndexOf("\"messages\"", StringComparison.Ordinal);
            if (idx < 0) return Truncate(json, 600);
            return Truncate(json.Substring(idx), 600);
        }

        /// <summary>
        /// history JSON の "outputs" 内から出力画像を選ぶ。type=="output" を優先し、
        /// 無ければ最初に見つかった画像を使う (temp/preview の取り違えを避ける)。
        /// </summary>
        static bool TrySelectOutputImage(string json, out string filename, out string subfolder, out string viewType)
        {
            filename = null; subfolder = ""; viewType = "output";
            int outputsIdx = json.IndexOf("\"outputs\"", StringComparison.Ordinal);
            int pos = outputsIdx >= 0 ? outputsIdx : 0;
            string firstFile = null, firstSub = "", firstType = "output";
            bool any = false;
            while (true)
            {
                int fStart = FindJsonStringValue(json, "filename", pos);
                if (fStart < 0) break;
                string fn = ReadJsonStringFrom(json, fStart);
                if (string.IsNullOrEmpty(fn)) { pos = fStart + 1; continue; }
                int objEnd = json.IndexOf('}', fStart); // 当該 image オブジェクト境界に限定
                string sub = ExtractJsonStringAllowEmptyBounded(json, "subfolder", fStart, objEnd) ?? "";
                string ty = ExtractJsonStringBounded(json, "type", fStart, objEnd) ?? "output";
                if (!any) { firstFile = fn; firstSub = sub; firstType = ty; any = true; }
                if (ty == "output") { filename = fn; subfolder = sub; viewType = ty; return true; }
                pos = fStart + 1;
            }
            if (!any) return false;
            filename = firstFile; subfolder = firstSub; viewType = firstType;
            return true;
        }

        /// <summary>node_errors が非空オブジェクトか。値がオブジェクトなので専用スキャン。</summary>
        static bool HasNodeErrors(string json)
        {
            int keyIdx = json.IndexOf("\"node_errors\"", StringComparison.Ordinal);
            if (keyIdx < 0) return false;
            int i = keyIdx + "\"node_errors\"".Length;
            // ':' まで
            while (i < json.Length && json[i] != ':') { if (!char.IsWhiteSpace(json[i])) return false; i++; }
            i++; // skip ':'
            // 次の非空白
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '{') return false; // オブジェクトでない => エラー無し扱い
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            // '{' の直後が '}' なら空 = エラー無し
            return !(i < json.Length && json[i] == '}');
        }

        /// <summary>history レスポンスに当該 prompt の entry が書かれた(=terminal)か。空 "{}" は実行中。</summary>
        static bool HasHistoryEntry(string json)
        {
            return json.IndexOf("\"outputs\"", StringComparison.Ordinal) >= 0
                || json.IndexOf("\"status\"", StringComparison.Ordinal) >= 0;
        }

        /// <summary>key の値 (文字列) の開始 index (開いた " の次) を返す。GeminiImageProvider と同パターン。</summary>
        static int FindJsonStringValue(string json, string key, int startIndex)
        {
            string keyPattern = "\"" + key + "\"";
            int keyIdx = json.IndexOf(keyPattern, startIndex, StringComparison.Ordinal);
            if (keyIdx < 0) return -1;

            int afterKey = keyIdx + keyPattern.Length;
            int colonIdx = -1;
            for (int i = afterKey; i < json.Length; i++)
            {
                char c = json[i];
                if (c == ':') { colonIdx = i; break; }
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') return -1;
            }
            if (colonIdx < 0) return -1;

            for (int i = colonIdx + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') return i + 1;
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') return -1;
            }
            return -1;
        }

        static string Truncate(string s, int maxLen)
        {
            if (s == null) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }

        // ─── 内蔵 img2img workflow 雛形 (API 形式・プレースホルダ) ───
        // {{IMAGE}} {{CKPT}} {{POSITIVE}} {{NEGATIVE}} {{SAMPLER}} {{SCHEDULER}} は文字列値の中身、
        // {{SEED}} {{STEPS}} {{CFG}} {{DENOISE}} は数値。
        const string BuiltinImg2ImgTemplate =
            "{" +
            "\"4\":{\"class_type\":\"CheckpointLoaderSimple\",\"inputs\":{\"ckpt_name\":\"{{CKPT}}\"}}," +
            "\"10\":{\"class_type\":\"LoadImage\",\"inputs\":{\"image\":\"{{IMAGE}}\"}}," +
            "\"11\":{\"class_type\":\"VAEEncode\",\"inputs\":{\"pixels\":[\"10\",0],\"vae\":[\"4\",2]}}," +
            "\"6\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"{{POSITIVE}}\",\"clip\":[\"4\",1]}}," +
            "\"7\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"{{NEGATIVE}}\",\"clip\":[\"4\",1]}}," +
            "\"3\":{\"class_type\":\"KSampler\",\"inputs\":{\"seed\":{{SEED}},\"steps\":{{STEPS}},\"cfg\":{{CFG}},\"sampler_name\":\"{{SAMPLER}}\",\"scheduler\":\"{{SCHEDULER}}\",\"denoise\":{{DENOISE}},\"model\":[\"4\",0],\"positive\":[\"6\",0],\"negative\":[\"7\",0],\"latent_image\":[\"11\",0]}}," +
            "\"8\":{\"class_type\":\"VAEDecode\",\"inputs\":{\"samples\":[\"3\",0],\"vae\":[\"4\",2]}}," +
            "\"9\":{\"class_type\":\"SaveImage\",\"inputs\":{\"filename_prefix\":\"UnityAgent\",\"images\":[\"8\",0]}}" +
            "}";
    }
}
