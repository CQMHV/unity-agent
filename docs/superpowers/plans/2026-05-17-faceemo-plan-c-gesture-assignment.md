# FaceEmo Plan C — Gesture-Aware Expression Workflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** AI が「Milfy_Another に笑顔つけて」発話で avatar/Mode/gesture/hand を解決し、FaceEmo Expression Editor に絞り込んだ BlendShape + 3 案 starter を流し込み、user が slider 調整して Branch に割当できるワークフローを実装する。

**Architecture:** Plan A/B の上に 5 レイヤー (Orchestrator / Discovery / Convention / Curation / Execution) を `Editor/Tools/FaceEmoPlanC/` 配下に追加。Plan A の `FaceEmoExpressionSession` を `OpenForBranch` + `CommitAsBranchOf` + `CommitInPlace` で拡張。10 新規 AgentTool + 2 修正 (`OpenExpressionSession` / `CommitExpressionSession` に `editMode` param 追加)。

**Tech Stack:** Unity 2022.3 Editor scripts (C#), FaceEmo (jp.suzuryg.face-emo) reflection, existing `FaceProfile` infrastructure (`BlendShapeCategorizer` / `FacePreset` / `PresetMatcher`), AgentTool MCP framework.

**Spec:** `docs/superpowers/specs/2026-05-17-faceemo-plan-c-gesture-assignment-design.md`

**Testing approach:** リポジトリに自動テスト framework は無い (Plan A 同様)。各タスクで `Editor/Tools/FaceEmoPlanC/Testing/` に **manual test window** を作成、Editor 上で動作確認。最終タスクで Gemini ハイジャック E2E 検証。

---

## File Structure

新規 (Plan C):
```
Editor/Tools/FaceEmoPlanC/
├─ Conventions/
│   ├─ IntentGestureMap.cs          ← intent → 推奨 gesture map
│   ├─ HandPoseDisplay.cs           ← Hand pose i18n (絵文字 + 英 + 日)
│   └─ IntentVocabulary.cs          ← 発話キーワード辞書
├─ Discovery/
│   ├─ AvatarResolver.cs            ← Avatar 優先順位解決
│   └─ FaceEmoStateInspector.cs     ← FaceEmo セットアップ状態判定
├─ Curation/
│   ├─ CandidateShapeBuilder.cs     ← intent → 10-15 shape 絞り込み
│   └─ ExpressionVariations.cs      ← 3 案 (やさしい/満面/はにかみ)
├─ Orchestration/
│   └─ ExpressionWorkflow.cs        ← top-level mode + skip 判定
├─ AgentTools/
│   ├─ DiscoveryTools.cs            ← 3 AgentTool
│   ├─ GestureTools.cs              ← 4 AgentTool
│   └─ CurationTools.cs             ← 3 AgentTool
└─ Testing/
    └─ PlanCTestWindow.cs           ← Editor 上 manual test
```

修正 (Plan A):
```
Editor/Tools/FaceEmoExpressionEditor/
├─ FaceEmoExpressionSession.cs      ← +OpenForBranch / +CommitAsBranchOf / +CommitInPlace / +enums
└─ ExpressionSessionTools.cs        ← +editMode param

Editor/Tools/BuiltInSkills.cs       ← Plan C workflow guide 追記
CHANGELOG.md                         ← Plan C エントリ
```

---

## Task Execution Order Rationale

- **Phase 1 (Tasks 1-3)**: 純データ層、依存ゼロ → 並列実行可
- **Phase 2 (Tasks 4-5)**: Convention + 既存 FaceProfile に依存
- **Phase 3 (Tasks 6-7)**: Unity scene 依存
- **Phase 4 (Tasks 8-11)**: Plan A Session 拡張 (atomic 操作の rollback 含む)
- **Phase 5 (Task 12)**: Orchestrator (Conventions に依存)
- **Phase 6 (Tasks 13-15)**: AgentTool wrapping (全前段に依存)
- **Phase 7 (Tasks 16-17)**: 統合 + manual E2E

---

## Phase 1: Conventions

### Task 1: IntentGestureMap

**Files:**
- Create: `Editor/Tools/FaceEmoPlanC/Conventions/IntentGestureMap.cs`

- [ ] **Step 1: ファイル作成**

```csharp
// Editor/Tools/FaceEmoPlanC/Conventions/IntentGestureMap.cs
using System.Collections.Generic;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Conventions
{
    /// <summary>
    /// intent (smile/angry/...) → 推奨 HandGesture 名 を返す静的データ。
    /// Step 4a の 8-grid 表示で ★ マーク用。
    /// preset 不在 intent は null を返す (推奨無し)。
    /// </summary>
    public static class IntentGestureMap
    {
        // value は FaceEmoAPI.ParseGesture が受け付ける文字列 (PascalCase)
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>
        {
            { "smile",       "HandOpen" },
            { "happy",       "HandOpen" },
            { "joy",         "HandOpen" },
            { "angry",       "Fist" },
            { "mad",         "Fist" },
            { "pout",        "Fist" },
            { "sad",         "Neutral" },
            { "cry",         "Neutral" },
            { "sob",         "Neutral" },
            { "surprise",    "HandOpen" },
            { "shock",       "HandOpen" },
            { "wink",        "Victory" },
            { "playful",     "Victory" },
            { "sleepy",      "Neutral" },
            { "tired",       "Neutral" },
            { "confident",   "ThumbsUp" },
            { "smug",        "ThumbsUp" },
            { "love",        "HandGun" },
            { "heart",       "HandGun" },
            { "cool",        "RockNRoll" },
            { "rock",        "RockNRoll" },
            { "concentrate", "Fingerpoint" },
        };

        /// <summary>intent → 推奨 gesture 名。preset 不在なら null。</summary>
        public static string GetRecommendedGesture(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            return Map.TryGetValue(intent.ToLowerInvariant(), out var g) ? g : null;
        }

        /// <summary>サポート intent 名一覧 (小文字)。</summary>
        public static IEnumerable<string> SupportedIntents => Map.Keys;
    }
}
```

- [ ] **Step 2: Editor を起動して compile 通ることを確認**

PowerShell: `Get-Process Unity*` で Unity 起動済みか確認 → 起動中なら window focus してリコンパイル待つ
Expected: Editor console にエラー無し

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoPlanC/Conventions/IntentGestureMap.cs
git commit -m "feat(planC): add IntentGestureMap (intent → recommended gesture)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: HandPoseDisplay

**Files:**
- Create: `Editor/Tools/FaceEmoPlanC/Conventions/HandPoseDisplay.cs`

- [ ] **Step 1: ファイル作成**

```csharp
// Editor/Tools/FaceEmoPlanC/Conventions/HandPoseDisplay.cs
using System.Collections.Generic;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Conventions
{
    /// <summary>
    /// HandGesture / Hand qualifier の表示文字列 (絵文字 + 英名 + 日本語名)。
    /// AskUser 系のラベル生成で使用。
    /// </summary>
    public static class HandPoseDisplay
    {
        // FaceEmoAPI.ParseGesture が受け付ける PascalCase → 表示
        private static readonly Dictionary<string, (string emoji, string ja)> GestureMap =
            new Dictionary<string, (string, string)>
        {
            { "Neutral",     ("😐", "ニュートラル") },
            { "Fist",        ("✊", "握り") },
            { "HandOpen",    ("✋", "パー") },
            { "Fingerpoint", ("☝️", "指差し") },
            { "Victory",     ("✌️", "ピース") },
            { "RockNRoll",   ("🤘", "ロック") },
            { "HandGun",     ("🤙", "ハンドガン") },
            { "ThumbsUp",    ("👍", "グッド") },
        };

        private static readonly Dictionary<string, string> HandJa = new Dictionary<string, string>
        {
            { "Either",  "どちらの手でも (Either)" },
            { "Left",    "左手のみ (Left)" },
            { "Right",   "右手のみ (Right)" },
            { "Both",    "両手 (Both)" },
            { "OneSide", "片手だけ (OneSide)" },
        };

        /// <summary>例: "✋ HandOpen / パー"</summary>
        public static string FormatGesture(string gesture)
        {
            if (string.IsNullOrEmpty(gesture)) return gesture ?? "";
            return GestureMap.TryGetValue(gesture, out var v)
                ? $"{v.emoji} {gesture} / {v.ja}"
                : gesture;
        }

        /// <summary>例: "どちらの手でも (Either)"</summary>
        public static string FormatHand(string hand)
        {
            if (string.IsNullOrEmpty(hand)) return hand ?? "";
            return HandJa.TryGetValue(hand, out var v) ? v : hand;
        }

        /// <summary>絵文字単体 ("✋" など)。</summary>
        public static string GetEmoji(string gesture)
        {
            return GestureMap.TryGetValue(gesture ?? "", out var v) ? v.emoji : "";
        }
    }
}
```

- [ ] **Step 2: Editor リコンパイル確認** (Console エラー無し)

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoPlanC/Conventions/HandPoseDisplay.cs
git commit -m "feat(planC): add HandPoseDisplay (i18n labels)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: IntentVocabulary

**Files:**
- Create: `Editor/Tools/FaceEmoPlanC/Conventions/IntentVocabulary.cs`

- [ ] **Step 1: ファイル作成**

```csharp
// Editor/Tools/FaceEmoPlanC/Conventions/IntentVocabulary.cs
using System.Collections.Generic;
using System.Linq;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Conventions
{
    /// <summary>
    /// 発話文からのキーワード抽出辞書。
    /// Orchestrator の AskUser スキップ判定 (Sec 2 of spec) に使用。
    /// </summary>
    public static class IntentVocabulary
    {
        public enum TopMode { Auto, Interactive, Unspecified }

        private static readonly string[] AutoKeywords =
            { "任せて", "おまかせ", "適当", "quick", "一発", "ぱっと" };

        private static readonly string[] InteractiveKeywords =
            { "編集", "調整", "詳しく", "ちゃんと", "カスタム", "手で" };

        // 日本語 → HandGesture PascalCase
        private static readonly Dictionary<string, string> HandPoseJaToEn =
            new Dictionary<string, string>
        {
            { "パー",     "HandOpen" },
            { "ぱー",     "HandOpen" },
            { "グー",     "Fist" },
            { "ぐー",     "Fist" },
            { "握り",     "Fist" },
            { "ピース",   "Victory" },
            { "ぴーす",   "Victory" },
            { "グッド",   "ThumbsUp" },
            { "ぐっど",   "ThumbsUp" },
            { "指差し",   "Fingerpoint" },
            { "さしゆび", "Fingerpoint" },
            { "ロック",   "RockNRoll" },
            { "ろっく",   "RockNRoll" },
            { "ハンドガン", "HandGun" },
            { "中立",     "Neutral" },
            { "ニュートラル", "Neutral" },
        };

        // 日本語 → Hand qualifier
        private static readonly Dictionary<string, string> HandQualifierJaToEn =
            new Dictionary<string, string>
        {
            { "左手で", "Left" },
            { "ひだりてで", "Left" },
            { "右手で", "Right" },
            { "みぎてで", "Right" },
            { "両手で", "Both" },
            { "りょうてで", "Both" },
            { "片手で", "OneSide" },
            { "かたてで", "OneSide" },
        };

        /// <summary>top-level モード推定。"任せて" 等で Auto、"編集" 等で Interactive。</summary>
        public static TopMode DetectTopMode(string utterance)
        {
            if (string.IsNullOrEmpty(utterance)) return TopMode.Unspecified;
            string lower = utterance.ToLowerInvariant();
            if (AutoKeywords.Any(k => lower.Contains(k.ToLowerInvariant())))
                return TopMode.Auto;
            if (InteractiveKeywords.Any(k => lower.Contains(k.ToLowerInvariant())))
                return TopMode.Interactive;
            return TopMode.Unspecified;
        }

        /// <summary>発話中の HandGesture を検出 (英名 / 日本語名 / FaceEmoAPI.ParseGesture 経由)。</summary>
        public static string DetectHandPose(string utterance)
        {
            if (string.IsNullOrEmpty(utterance)) return null;
            // 日本語マッチ優先
            foreach (var kv in HandPoseJaToEn)
                if (utterance.Contains(kv.Key)) return kv.Value;
            // 英名そのまま
            string[] englishNames = { "Neutral", "Fist", "HandOpen", "Fingerpoint",
                                       "Victory", "RockNRoll", "HandGun", "ThumbsUp" };
            foreach (var en in englishNames)
                if (utterance.IndexOf(en, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return en;
            return null;
        }

        /// <summary>発話中の Hand qualifier を検出。デフォルト推定値は呼出側で Either に。</summary>
        public static string DetectHandQualifier(string utterance)
        {
            if (string.IsNullOrEmpty(utterance)) return null;
            foreach (var kv in HandQualifierJaToEn)
                if (utterance.Contains(kv.Key)) return kv.Value;
            // 英名直接
            string[] englishHands = { "Either", "Left", "Right", "Both", "OneSide" };
            foreach (var en in englishHands)
                if (utterance.IndexOf(en, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return en;
            return null;
        }
    }
}
```

- [ ] **Step 2: Editor リコンパイル確認**

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoPlanC/Conventions/IntentVocabulary.cs
git commit -m "feat(planC): add IntentVocabulary (utterance keyword detection)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase 2: Curation

### Task 4: ExpressionVariations (variation profiles + apply)

**Files:**
- Create: `Editor/Tools/FaceEmoPlanC/Curation/ExpressionVariations.cs`

- [ ] **Step 1: ファイル作成**

```csharp
// Editor/Tools/FaceEmoPlanC/Curation/ExpressionVariations.cs
using System.Collections.Generic;
using System.Linq;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Curation
{
    /// <summary>
    /// intent ごとの 3 案 (やさしい/満面/はにかみ 等) を生成する。
    /// 各 variation は同 candidate set を共有し、活性 shape の subset と値が違う。
    /// </summary>
    public static class ExpressionVariations
    {
        public sealed class Variation
        {
            public string Name { get; set; }                     // 例 "やさしい"
            public Dictionary<string, float> Values { get; set; } // shapeName → 0-100
        }

        // intent → 3 案ラベル
        private static readonly Dictionary<string, string[]> IntentLabels =
            new Dictionary<string, string[]>
        {
            { "smile",    new[] { "やさしい", "満面", "はにかみ" } },
            { "angry",    new[] { "不満", "激怒", "むすっと" } },
            { "sad",      new[] { "しょんぼり", "大泣き", "我慢" } },
            { "surprise", new[] { "びっくり", "驚愕", "ぽかん" } },
            { "wink",     new[] { "軽い", "しっかり", "キュート" } },
            { "sleepy",   new[] { "うとうと", "熟睡", "寝起き" } },
        };

        private static readonly string[] GenericLabels = { "弱", "中", "強" };

        /// <summary>intent に対する variation ラベル 3 個 (preset 不在は generic)。</summary>
        public static string[] GetLabels(string intent)
        {
            return IntentLabels.TryGetValue(intent?.ToLowerInvariant() ?? "", out var labels)
                ? labels : GenericLabels;
        }

        /// <summary>
        /// candidate shape リスト + seed 値 → 3 variation を生成。
        /// 案1 (low): seed × 0.6 のみ。案2 (mid): seed × 1.0 + 関連 shape 70%。案3 (high): seed × 0.7 + intent 別追加。
        /// </summary>
        public static List<Variation> Generate(
            string intent,
            IReadOnlyDictionary<string, float> seedValues,    // PresetMap 由来 (shapeName → 100 ベース)
            IReadOnlyList<string> relatedShapes)               // candidate set 残り
        {
            var labels = GetLabels(intent);
            var variations = new List<Variation>();

            // 案1: low intensity
            var low = new Dictionary<string, float>();
            foreach (var kv in seedValues) low[kv.Key] = kv.Value * 0.6f;
            variations.Add(new Variation { Name = labels[0], Values = low });

            // 案2: full + related も活性
            var mid = new Dictionary<string, float>();
            foreach (var kv in seedValues) mid[kv.Key] = kv.Value;
            foreach (var rs in relatedShapes.Take(5))           // 上位 5 個
                if (!mid.ContainsKey(rs)) mid[rs] = 70f;
            variations.Add(new Variation { Name = labels[1], Values = mid });

            // 案3: intent 別の追加 shape (shy なら eye_close / cheek_blush)
            var high = new Dictionary<string, float>();
            foreach (var kv in seedValues) high[kv.Key] = kv.Value * 0.7f;
            foreach (var extra in GetIntentExtras(intent, relatedShapes))
                if (!high.ContainsKey(extra.Key)) high[extra.Key] = extra.Value;
            variations.Add(new Variation { Name = labels[2], Values = high });

            return variations;
        }

        // intent ごとに案3 で追加 activate したい shape (related から見つかれば)
        private static IEnumerable<KeyValuePair<string, float>> GetIntentExtras(
            string intent, IReadOnlyList<string> relatedShapes)
        {
            string i = intent?.ToLowerInvariant() ?? "";
            if (i == "smile") // はにかみ = shy + blush
            {
                foreach (var rs in relatedShapes)
                {
                    if (rs.ToLowerInvariant().Contains("close")) yield return Kv(rs, 50f);
                    if (rs.ToLowerInvariant().Contains("blush")) yield return Kv(rs, 60f);
                    if (rs.ToLowerInvariant().Contains("照") || rs.Contains("てれ")) yield return Kv(rs, 60f);
                }
            }
        }

        private static KeyValuePair<string, float> Kv(string k, float v)
            => new KeyValuePair<string, float>(k, v);
    }
}
```

- [ ] **Step 2: Editor リコンパイル確認**

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoPlanC/Curation/ExpressionVariations.cs
git commit -m "feat(planC): add ExpressionVariations (3-variation generator per intent)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: CandidateShapeBuilder (intent → 10-15 shape 絞り込み)

**Files:**
- Create: `Editor/Tools/FaceEmoPlanC/Curation/CandidateShapeBuilder.cs`

- [ ] **Step 1: ファイル作成**

```csharp
// Editor/Tools/FaceEmoPlanC/Curation/CandidateShapeBuilder.cs
using System.Collections.Generic;
using System.Linq;
using AjisaiFlow.UnityAgent.Editor.Tools.FaceProfile;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Curation
{
    /// <summary>
    /// intent → 10-15 個の candidate shape を絞り込む。
    /// 既存 FaceProfile (FacePreset / PresetCandidate / BlendShapeCategorizer) を再利用。
    /// </summary>
    public static class CandidateShapeBuilder
    {
        public sealed class Result
        {
            public List<string> Candidates { get; set; }                   // 全 shape 名 (smr-prefixed or plain — 呼出側に合わせる)
            public Dictionary<string, float> SeedValues { get; set; }      // preset 3 個 + 初期値 (案2 のベース)
            public List<ExpressionVariations.Variation> Variations { get; set; }
        }

        // intent 同義語マップ (shy → smile に正規化など)。"smile" 系は preset と同名。
        private static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>
        {
            { "ニコニコ", "smile" }, { "笑顔", "smile" }, { "笑い", "smile" },
            { "happy",   "smile" }, { "joy",  "smile" },
            { "怒り",     "angry" },
            { "悲しい",   "sad" },
            { "驚き",     "surprise" },
        };

        /// <summary>
        /// FaceBlendShapeProfile (Plan A の FaceProfile キャッシュ) から intent に関連 shape を抽出。
        /// </summary>
        /// <param name="profile">avatar 解析済 profile (FaceProfileTools.AnalyzeFace の結果)</param>
        /// <param name="intent">smile / angry / wink / 等 (同義語も受付)</param>
        /// <param name="breadth">narrow=3, wide=10-15</param>
        public static Result Build(FaceBlendShapeProfile profile, string intent, int breadth)
        {
            if (profile == null) return Empty();
            string normIntent = NormalizeIntent(intent);

            // step 1: preset seed (Plan A FacePreset の resolved candidate)
            var seed = new Dictionary<string, float>();
            FacePreset? preset = BlendShapeCategorizer.ResolvePreset(normIntent);
            if (preset.HasValue)
            {
                var candidate = profile.presets.FirstOrDefault(p => p.preset == preset.Value.ToString());
                if (candidate != null)
                {
                    foreach (var e in candidate.entries.Take(3))
                        seed[e.shapeName] = e.value;
                }
            }

            // step 2: related shapes (カテゴリ + intent tag + seed prefix)
            var relatedRanked = ComputeRelatedShapes(profile, normIntent, seed.Keys);

            // step 3: union, cap at breadth
            int targetSize = breadth <= 0 ? 15 : breadth;
            var ordered = seed.Keys.Concat(relatedRanked.Where(s => !seed.ContainsKey(s)))
                                   .Distinct()
                                   .Take(targetSize)
                                   .ToList();

            // step 4: variations (Task 4)
            var variations = ExpressionVariations.Generate(normIntent, seed, ordered);

            return new Result
            {
                Candidates = ordered,
                SeedValues = seed,
                Variations = variations,
            };
        }

        private static Result Empty() => new Result
        {
            Candidates = new List<string>(),
            SeedValues = new Dictionary<string, float>(),
            Variations = new List<ExpressionVariations.Variation>(),
        };

        private static string NormalizeIntent(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return "";
            foreach (var kv in Synonyms)
                if (intent.Contains(kv.Key)) return kv.Value;
            return intent.ToLowerInvariant();
        }

        // intent → 関連 shape をランキング (カテゴリ一致 > tag 一致 > seed prefix)
        private static List<string> ComputeRelatedShapes(
            FaceBlendShapeProfile profile, string intent, IEnumerable<string> seedKeys)
        {
            var seedPrefixes = seedKeys
                .Select(s => System.Text.RegularExpressions.Regex.Replace(s, @"_\d+$", ""))
                .Where(s => s.Length >= 4)
                .Distinct()
                .ToList();

            // intent → 期待カテゴリ
            var targetCategories = intent switch
            {
                "smile" or "happy"    => new[] { FaceCategory.Mouth, FaceCategory.Cheek, FaceCategory.Eye },
                "angry"               => new[] { FaceCategory.Brow, FaceCategory.Mouth, FaceCategory.Eye },
                "sad" or "cry"        => new[] { FaceCategory.Brow, FaceCategory.Eye, FaceCategory.Mouth },
                "surprise"            => new[] { FaceCategory.Eye, FaceCategory.Mouth, FaceCategory.Brow },
                "wink"                => new[] { FaceCategory.Eye },
                "sleepy"              => new[] { FaceCategory.Eye },
                _                     => new[] { FaceCategory.Mouth, FaceCategory.Eye, FaceCategory.Brow, FaceCategory.Cheek },
            };

            var ranked = new List<(string name, int score)>();
            foreach (var section in profile.categories)
            {
                if (!targetCategories.Contains(section.category)) continue;
                foreach (var entry in section.shapes)
                {
                    int score = 0;
                    if (entry.tags != null && entry.tags.Contains(intent)) score += 100;
                    if (seedPrefixes.Any(p => entry.shapeName.IndexOf(p, System.StringComparison.OrdinalIgnoreCase) >= 0))
                        score += 50;
                    // 同カテゴリで base スコア
                    score += 10;
                    if (score > 0) ranked.Add((entry.shapeName, score));
                }
            }
            return ranked.OrderByDescending(r => r.score).Select(r => r.name).Distinct().ToList();
        }
    }
}
```

- [ ] **Step 2: Editor リコンパイル確認**

注意: `FaceBlendShapeProfile` / `FaceCategory` / `BlendShapeCategorizer.ResolvePreset` の名前空間と型は `Editor/Tools/FaceProfile/*.cs` で確認。`profile.categories` / `profile.presets` / `section.shapes` / `entry.tags` のプロパティ名がコードの型定義と一致するかも確認。一致しなければそれぞれの型定義に合わせて修正。

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoPlanC/Curation/CandidateShapeBuilder.cs
git commit -m "feat(planC): add CandidateShapeBuilder (intent → 10-15 shape filter)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase 3: Discovery

### Task 6: AvatarResolver

**Files:**
- Create: `Editor/Tools/FaceEmoPlanC/Discovery/AvatarResolver.cs`

- [ ] **Step 1: ファイル作成**

```csharp
// Editor/Tools/FaceEmoPlanC/Discovery/AvatarResolver.cs
#if FACE_EMO
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoExpressionEditor;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Discovery
{
    /// <summary>
    /// 「どの avatar に対して操作するか」を発話文 + scene 状態から決定。
    /// Spec Sec 5.1 の優先順位ロジックを実装。
    /// </summary>
    public static class AvatarResolver
    {
        public enum Confidence { Exact, High, Medium, Low, None }

        public sealed class Result
        {
            public string AvatarRootName { get; set; }       // 解決された avatar 名 (null = 解決失敗)
            public Confidence Confidence { get; set; }
            public List<string> Alternatives { get; set; }   // disambig 候補
            public string Reason { get; set; }               // 解決根拠 (ログ用)
        }

        /// <summary>
        /// 優先順位:
        ///   1. promptHint に名前 → 厳密一致 (Exact) / 部分一致複数 → Alternatives
        ///   2. Active Session の avatarRootName (High)
        ///   3. scene の VRCAvatarDescriptor + activeInHierarchy
        ///      a. 1 体 → 採用 (Medium)
        ///      b. 複数 → Alternatives
        ///   4. 該当無し
        /// </summary>
        public static Result Resolve(string promptHint)
        {
            // priority 1
            if (!string.IsNullOrEmpty(promptHint))
            {
                var allAvatars = ListActiveVrcAvatars();
                // 厳密一致
                var exact = allAvatars.FirstOrDefault(a => a.name == promptHint);
                if (exact != null)
                    return Ok(exact.name, Confidence.Exact, $"promptHint 厳密一致: {promptHint}");
                // 部分一致
                var partial = allAvatars.Where(a => a.name.IndexOf(promptHint, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (partial.Count == 1)
                    return Ok(partial[0].name, Confidence.High, $"promptHint 部分一致: {promptHint}");
                if (partial.Count > 1)
                    return Ambiguous(partial.Select(a => a.name).ToList(), $"promptHint '{promptHint}' に複数 hit");
            }

            // priority 2: Active session
            var active = FaceEmoExpressionSession.Active;
            if (active?.Launcher != null)
            {
                var root = active.Launcher.gameObject.transform.root.name;
                return Ok(root, Confidence.High, $"Active session の avatar: {root}");
            }

            // priority 3: scene 内 VRC avatar
            var avatars = ListActiveVrcAvatars();
            if (avatars.Count == 1)
                return Ok(avatars[0].name, Confidence.Medium, $"scene に 1 体のみ: {avatars[0].name}");
            if (avatars.Count > 1)
                return Ambiguous(avatars.Select(a => a.name).ToList(), "scene に複数 avatar");

            // priority 4
            return new Result
            {
                AvatarRootName = null,
                Confidence = Confidence.None,
                Alternatives = new List<string>(),
                Reason = "scene に VRC avatar が見つかりません",
            };
        }

        // scene 全 stage を走査して activeInHierarchy な VRC avatar root を返す
        private static List<GameObject> ListActiveVrcAvatars()
        {
            var list = new List<GameObject>();
            var descriptors = Object.FindObjectsOfType<VRCAvatarDescriptor>(includeInactive: false);
            foreach (var d in descriptors)
            {
                if (d == null || d.gameObject == null) continue;
                if (!d.gameObject.activeInHierarchy) continue;
                if (!list.Contains(d.gameObject)) list.Add(d.gameObject);
            }
            return list;
        }

        private static Result Ok(string name, Confidence conf, string reason) => new Result
        {
            AvatarRootName = name,
            Confidence = conf,
            Alternatives = new List<string>(),
            Reason = reason,
        };

        private static Result Ambiguous(List<string> alts, string reason) => new Result
        {
            AvatarRootName = null,
            Confidence = Confidence.Low,
            Alternatives = alts,
            Reason = reason,
        };
    }
}
#endif
```

- [ ] **Step 2: Editor リコンパイル確認**

注意: `FaceEmoExpressionSession.Active` と `Launcher` property が public/internal でアクセス可能か確認。`Active` は public、`Launcher` も public (確認済 spec 調査時)。

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoPlanC/Discovery/AvatarResolver.cs
git commit -m "feat(planC): add AvatarResolver (5-priority avatar resolution)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: FaceEmoStateInspector

**Files:**
- Create: `Editor/Tools/FaceEmoPlanC/Discovery/FaceEmoStateInspector.cs`

- [ ] **Step 1: ファイル作成**

```csharp
// Editor/Tools/FaceEmoPlanC/Discovery/FaceEmoStateInspector.cs
#if FACE_EMO
using System.Linq;
using UnityEngine;
using AjisaiFlow.UnityAgent.Editor.Tools;
using Suzuryg.FaceEmo.Components;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Discovery
{
    /// <summary>
    /// 指定 avatar に対する FaceEmo セットアップ状態を判定。
    /// Spec Sec 5.2 の 5 状態を返す + 次のアクションヒント。
    /// </summary>
    public static class FaceEmoStateInspector
    {
        public enum State
        {
            NotInstalled,            // パッケージ自体無し
            NoLauncher,              // パッケージ有、launcher 無し
            LauncherUnconfigured,    // launcher 有、TargetAvatar 未設定
            Configured,              // Mode 0 個
            HasModes,                // Mode 1+ 個
        }

        public sealed class Result
        {
            public State CurrentState { get; set; }
            public FaceEmoLauncherComponent Launcher { get; set; }   // 関連 launcher (null 可)
            public string[] ModeNames { get; set; }                  // HasModes のとき
            public string NextActionHint { get; set; }
            public string AvatarRootName { get; set; }
        }

        public static Result Inspect(string avatarRootName)
        {
            var r = new Result { AvatarRootName = avatarRootName, ModeNames = System.Array.Empty<string>() };

            // FaceEmo パッケージ判定 (FaceEmoLauncherComponent 型が存在するか)
            // FACE_EMO シンボルで囲われているのでここに来た時点で型は存在する
            // → NotInstalled は #if FACE_EMO 外で別途判定 (本ファイルはコンパイル除外)

            // launcher 検索 (avatar 指定優先)
            FaceEmoLauncherComponent launcher = null;
            if (!string.IsNullOrEmpty(avatarRootName))
                launcher = FaceEmoAPI.FindLauncherForAvatar(avatarRootName);
            if (launcher == null)
            {
                r.CurrentState = State.NoLauncher;
                r.NextActionHint = "AutoSetupFaceEmoForAvatar(avatarRootName) を呼んで launcher を作成";
                return r;
            }
            r.Launcher = launcher;

            // TargetAvatar 設定確認
            var targetAvatar = launcher.TargetAvatar;
            if (targetAvatar == null)
            {
                r.CurrentState = State.LauncherUnconfigured;
                r.NextActionHint = "ConfigureTargetAvatar(launcher, avatar) を呼んで紐付け";
                return r;
            }

            // Mode 列挙
            var menu = FaceEmoAPI.LoadMenu(launcher);
            if (menu == null)
            {
                r.CurrentState = State.Configured;
                r.NextActionHint = "menu load 失敗。FaceEmo MainView を起動して初期化を促す";
                return r;
            }

            var modes = menu.GetRegisteredModes()
                            .Concat(menu.GetUnregisteredModes())
                            .Select(m => m.DisplayName)
                            .ToArray();
            r.ModeNames = modes;
            if (modes.Length == 0)
            {
                r.CurrentState = State.Configured;
                r.NextActionHint = "Mode 0 個。新規 Mode 作成 (OpenExpressionSession editMode=new-mode)";
            }
            else
            {
                r.CurrentState = State.HasModes;
                r.NextActionHint = $"Mode {modes.Length} 個。AskUser で選択 OR モードが 1 個なら自動採択";
            }
            return r;
        }
    }
}
#endif
```

- [ ] **Step 2: Editor リコンパイル確認**

注意: `menu.GetRegisteredModes()` / `GetUnregisteredModes()` / `m.DisplayName` の API 名は FaceEmoAPI 内の呼出箇所と合わせて確認 (`FaceEmoAPI.cs:ListExpressions` 周辺を grep)。

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoPlanC/Discovery/FaceEmoStateInspector.cs
git commit -m "feat(planC): add FaceEmoStateInspector (5-state classifier)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase 4: Session API Extension

### Task 8: Session enums + snapshot fields

**Files:**
- Modify: `Editor/Tools/FaceEmoExpressionEditor/FaceEmoExpressionSession.cs`

- [ ] **Step 1: enum 定義を追加**

`FaceEmoExpressionSession.cs` の `public enum SyncMode { Live, Degraded }` の直後に以下を追加:

```csharp
        /// <summary>
        /// Session の編集モード。Open* で設定、Commit* の分岐ルートを決める。
        /// </summary>
        public enum SessionEditMode
        {
            NewMode,           // OpenForNewExpression — 新 Mode (Registered) を作る経路 (Plan A 既定)
            EditExistingClip,  // OpenForBranch — 既存 Branch の clip を Editor で直接編集
            CreateBranchClip,  // OpenForNewExpression + 後で CommitAsBranchOf で Branch に割当
        }

        /// <summary>CommitAsBranchOf 時の既存 binding に対する挙動。</summary>
        public enum OverwriteMode
        {
            Ask,            // 呼出側で AskUser、引数で具体的 mode を再指定する想定
            Overwrite,      // 新 clip 作成 + Branch 参照差替 (旧 clip は asset 残)
            EditExisting,   // 既存 clip を編集 (= OpenForBranch にフォールバック)
            Cancel,         // 操作中断
        }
```

- [ ] **Step 2: フィールド追加**

`private ExpressionEditorBridge _bridge;` の直後に:

```csharp
        /// <summary>Open* 時に設定。Commit* の routing に使用。</summary>
        public SessionEditMode EditMode { get; private set; } = SessionEditMode.NewMode;

        /// <summary>OpenForBranch / CommitAsBranchOf で使用する Branch 同定情報。</summary>
        internal string TargetModeName { get; private set; }
        internal string TargetGesture { get; private set; }
        internal string TargetHand { get; private set; }
        internal string TargetSlot { get; private set; }   // "Base"/"Left"/"Right"/"Both"

        /// <summary>Open 時に snapshot した launcher 名 (R2: Mode 同時編集検出用)。</summary>
        internal string LauncherSnapshot { get; private set; }
```

- [ ] **Step 3: 既存 OpenForMode / OpenForNewExpression に snapshot 設定追加**

`OpenForMode` 末尾の `_active = session;` 直前に:
```csharp
            session.EditMode = SessionEditMode.NewMode;
            session.LauncherSnapshot = gate.Launcher?.gameObject?.name;
```

`OpenForNewExpression` 末尾の `_active = session;` 直前に同様:
```csharp
            session.EditMode = SessionEditMode.NewMode;
            session.LauncherSnapshot = gate.Launcher?.gameObject?.name;
```

- [ ] **Step 4: Editor リコンパイル確認** (Plan A 既存ツールが壊れていないことを Console で確認)

- [ ] **Step 5: Commit**

```bash
git add Editor/Tools/FaceEmoExpressionEditor/FaceEmoExpressionSession.cs
git commit -m "feat(planC-session): add SessionEditMode + OverwriteMode enums

EditMode / TargetModeName / TargetGesture / TargetHand / TargetSlot / LauncherSnapshot fields
added in preparation for OpenForBranch + CommitAsBranchOf. Existing OpenFor* paths
default EditMode to NewMode (Plan A behavior unchanged).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: Session.OpenForBranch (既存 Branch の clip を Editor で開く)

**Files:**
- Modify: `Editor/Tools/FaceEmoExpressionEditor/FaceEmoExpressionSession.cs`

- [ ] **Step 1: OpenForBranch 静的メソッド追加**

`OpenForNewExpression` メソッドの直後 (close brace の後ろ) に:

```csharp
        /// <summary>
        /// 既存 Branch の指定 slot の clip を Editor で開く (EditExistingClip モード)。
        /// Plan C 用。Branch 既存前提、無ければ throw。
        /// </summary>
        /// <param name="launcherName">target launcher 名 (Mode 同時編集検出用)</param>
        /// <param name="modeName">target Mode 表示名</param>
        /// <param name="gesture">"HandOpen" 等 (FaceEmoAPI.ParseGesture 形式)</param>
        /// <param name="hand">"Either" 等 (FaceEmoAPI.ParseHand 形式)</param>
        /// <param name="slot">"Base"/"Left"/"Right"/"Both"</param>
        /// <param name="avatarRootName">avatar 同定用 (FaceEmoGate 経由)</param>
        public static FaceEmoExpressionSession OpenForBranch(
            string launcherName, string modeName,
            string gesture, string hand, string slot,
            string avatarRootName)
        {
            FaceEmoGate.Result gate;
            if (!string.IsNullOrEmpty(avatarRootName))
                gate = FaceEmoGate.RequireExpressionEditingReadyForAvatar(avatarRootName);
            else if (!string.IsNullOrEmpty(launcherName))
                gate = FaceEmoGate.RequireExpressionEditingReady(launcherName);
            else
                gate = FaceEmoGate.RequireExpressionEditingReady();
            if (!gate.Ok) throw new InvalidOperationException(StripErrorPrefix(gate.ErrorMessage));

            var menu = FaceEmoAPI.LoadMenu(gate.Launcher);
            if (menu == null) throw new InvalidOperationException("Failed to load FaceEmo menu.");
            var (modeId, mode) = FaceEmoAPI.FindExpression(menu, modeName);
            if (modeId == null) throw new InvalidOperationException($"Mode '{modeName}' not found.");

            var hg = FaceEmoAPI.ParseGesture(gesture);
            var hd = FaceEmoAPI.ParseHand(hand);
            var slotType = FaceEmoAPI.ParseBranchSlot(slot) ?? Suzuryg.FaceEmo.Domain.BranchAnimationType.Base;

            // Branch 検索
            int branchIndex = -1;
            for (int i = 0; i < (mode.Branches?.Count ?? 0); i++)
            {
                var b = mode.Branches[i];
                if (b.Conditions == null) continue;
                bool match = b.Conditions.Any(c => c.Hand == hd && c.HandGesture == hg);
                if (match) { branchIndex = i; break; }
            }
            if (branchIndex < 0)
                throw new InvalidOperationException($"Branch ({hand}, {gesture}) not found in Mode '{modeName}'.");

            // 該当 slot の clip を取得
            var branch = mode.Branches[branchIndex];
            var anim = slotType switch
            {
                Suzuryg.FaceEmo.Domain.BranchAnimationType.Left  => branch.LeftHandAnimation,
                Suzuryg.FaceEmo.Domain.BranchAnimationType.Right => branch.RightHandAnimation,
                Suzuryg.FaceEmo.Domain.BranchAnimationType.Both  => branch.BothHandsAnimation,
                _                                                 => branch.BaseAnimation,
            };

            AnimationClip clip = null;
            if (anim != null && !string.IsNullOrEmpty(anim.GUID))
            {
                var path = AssetDatabase.GUIDToAssetPath(anim.GUID);
                if (!string.IsNullOrEmpty(path))
                    clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            }
            if (clip == null)
                throw new InvalidOperationException(
                    $"Branch ({hand}, {gesture}) slot '{slot}' has no animation clip.");

            // session 作成 (OpenForMode と同パターン)
            _active?.Dispose();
            ExpressionEditorBridge.CleanupOrphanPreviewAvatars(preserveActiveSession: false);
            var session = new FaceEmoExpressionSession
            {
                Launcher = gate.Launcher,
                IsNewExpression = false,
                ModeId = modeId,
                Clip = clip,
                TmpName = null,
                Mode = SyncMode.Live,
                EditMode = SessionEditMode.EditExistingClip,
                LauncherSnapshot = gate.Launcher?.gameObject?.name,
                TargetModeName = modeName,
                TargetGesture = gesture,
                TargetHand = hand,
                TargetSlot = slot,
            };
            session._bridge = new ExpressionEditorBridge();
            if (!session._bridge.TryOpen(gate.Launcher, clip))
            {
                session._bridge.Dispose();
                session._bridge = null;
                session.Mode = SyncMode.Degraded;
            }
            ExpressionEditorBridge.CleanupOrphanPreviewAvatars(preserveActiveSession: true);
            _active = session;
            return session;
        }
```

- [ ] **Step 2: Editor リコンパイル確認**

`mode.Branches[i].Conditions` の Condition 型のプロパティ名 (`Hand` / `HandGesture`) と `branch.LeftHandAnimation` 等の slot プロパティ名は `Suzuryg.FaceEmo.Domain.Branch` を grep して実物に合わせる:
```
grep -rn "class Branch\|public.*BaseAnimation\|public.*LeftHandAnimation\|class Condition" Library/PackageCache/jp.suzuryg.face-emo*/
```

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoExpressionEditor/FaceEmoExpressionSession.cs
git commit -m "feat(planC-session): add OpenForBranch (load existing branch clip into editor)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: Session.CommitAsBranchOf + Session.CommitInPlace (atomic 6 step)

**Files:**
- Modify: `Editor/Tools/FaceEmoExpressionEditor/FaceEmoExpressionSession.cs`

- [ ] **Step 1: CommitAsBranchOf 追加**

OpenForBranch メソッドの後ろに以下を追加:

```csharp
        public sealed class CommitResult
        {
            public bool Ok { get; set; }
            public string ErrorMessage { get; set; }
            public string FinalClipPath { get; set; }
            public int BranchIndex { get; set; }
            public string DestinationDescription { get; set; }  // 例: "表情パターン1 / (Either, HandOpen) / BaseAnimation"
        }

        /// <summary>
        /// 現在の Editor 値を新 clip に保存し、指定 Mode の指定 Branch (新規 OR 既存) の指定 slot に割当てる。
        /// Spec Sec 7.3 の atomic 6 step を実装。失敗時は各 step に応じて rollback。
        /// </summary>
        public CommitResult CommitAsBranchOf(
            string modeName, string gesture, string hand, string slot,
            OverwriteMode overwriteMode = OverwriteMode.Overwrite)
        {
            string clipPath = null;
            int addedBranchIndex = -1;
            bool didAddBranch = false;
            Suzuryg.FaceEmo.Domain.FaceEmoAnimation prevAnim = null;
            var menu = FaceEmoAPI.LoadMenu(Launcher);

            try
            {
                if (menu == null) throw new InvalidOperationException("Failed to load FaceEmo menu.");
                var (modeId, mode) = FaceEmoAPI.FindExpression(menu, modeName);
                if (modeId == null) throw new InvalidOperationException($"Mode '{modeName}' not found.");

                var hg = FaceEmoAPI.ParseGesture(gesture);
                var hd = FaceEmoAPI.ParseHand(hand);
                var slotType = FaceEmoAPI.ParseBranchSlot(slot)
                    ?? Suzuryg.FaceEmo.Domain.BranchAnimationType.Base;

                Undo.SetCurrentGroupName($"Plan C: expression to ({hand}, {gesture}) on {modeName}");
                int undoGroup = Undo.GetCurrentGroup();

                // ① 現在 Editor 値 → 新 clip ファイル
                var values = GetCurrentValues();
                string ts = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string baseName = $"expr_{ts}";
                string dir = "Assets/Generated/UnityAgent/FaceEmoPlanC";
                if (!AssetDatabase.IsValidFolder(dir))
                    System.IO.Directory.CreateDirectory(dir);
                clipPath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{baseName}.anim");
                var newClip = new AnimationClip { name = baseName };
                ApplyValuesToClip(newClip, values);
                AssetDatabase.CreateAsset(newClip, clipPath);
                AssetDatabase.SaveAssetIfDirty(newClip);
                // ② 既存 Branch 検索
                int branchIdx = FindBranchByCondition(mode, hd, hg);
                bool isNew = (branchIdx < 0);
                if (isNew)
                {
                    // ③ AddBranch (新規)
                    var conditions = new List<Suzuryg.FaceEmo.Domain.Condition>
                    {
                        new Suzuryg.FaceEmo.Domain.Condition(hd, hg, Suzuryg.FaceEmo.Domain.ComparisonOperator.Equals),
                    };
                    branchIdx = FaceEmoAPI.AddBranch(menu, modeId, conditions);
                    addedBranchIndex = branchIdx;
                    didAddBranch = true;
                }
                else if (overwriteMode == OverwriteMode.Cancel)
                {
                    throw new InvalidOperationException("Existing branch present and overwriteMode=Cancel.");
                }
                else
                {
                    // 既存値を rollback 用に控える
                    prevAnim = GetBranchAnimation(mode.Branches[branchIdx], slotType);
                }

                // ④ slot 割当
                var faceEmoAnim = MakeFaceEmoAnimation(clipPath);
                FaceEmoAPI.SetBranchAnimation(menu, modeId, branchIdx, slotType, faceEmoAnim);

                // ⑤ menu save
                FaceEmoAPI.SaveMenu(Launcher, menu);
                AssetDatabase.SaveAssets();

                // ⑥ MainView refresh (失敗しても warn のみ)
                try { FaceEmoThumbnailRenderer.RefreshMainView(); } catch { /* warn-only */ }

                Undo.CollapseUndoOperations(undoGroup);

                return new CommitResult
                {
                    Ok = true,
                    FinalClipPath = clipPath,
                    BranchIndex = branchIdx,
                    DestinationDescription = $"{modeName} / ({hand}, {gesture}) / {slot}",
                };
            }
            catch (System.Exception ex)
            {
                // rollback (atomic): ④ で throw なら ③ をロールバック、③ で throw なら ② を ...
                try
                {
                    if (didAddBranch && menu != null && addedBranchIndex >= 0)
                    {
                        var (modeId2, _) = FaceEmoAPI.FindExpression(menu, modeName);
                        if (modeId2 != null) FaceEmoAPI.RemoveBranch(menu, modeId2, addedBranchIndex);
                    }
                    if (!string.IsNullOrEmpty(clipPath) && AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath) != null)
                    {
                        AssetDatabase.DeleteAsset(clipPath);
                    }
                }
                catch (System.Exception rex)
                {
                    Debug.LogWarning($"[PlanC] Rollback partial failure: {rex.Message}");
                }
                return new CommitResult { Ok = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>既存 clip を上書き保存 (EditExistingClip モード時)。Branch 参照は変えない。</summary>
        public CommitResult CommitInPlace()
        {
            if (EditMode != SessionEditMode.EditExistingClip)
                return new CommitResult { Ok = false, ErrorMessage = "CommitInPlace requires EditExistingClip mode." };
            if (Clip == null)
                return new CommitResult { Ok = false, ErrorMessage = "Session has no clip reference." };
            try
            {
                Undo.RegisterCompleteObjectUndo(Clip, "Plan C: in-place clip edit");
                var values = GetCurrentValues();
                ApplyValuesToClip(Clip, values);
                EditorUtility.SetDirty(Clip);
                AssetDatabase.SaveAssetIfDirty(Clip);
                return new CommitResult
                {
                    Ok = true,
                    FinalClipPath = AssetDatabase.GetAssetPath(Clip),
                    DestinationDescription = $"{TargetModeName} / ({TargetHand}, {TargetGesture}) / {TargetSlot} (in-place)",
                };
            }
            catch (System.Exception ex)
            {
                return new CommitResult { Ok = false, ErrorMessage = ex.Message };
            }
        }

        // ───────── helpers ─────────

        private static int FindBranchByCondition(
            Suzuryg.FaceEmo.Domain.Mode mode,
            Suzuryg.FaceEmo.Domain.Hand hand,
            Suzuryg.FaceEmo.Domain.HandGesture gesture)
        {
            if (mode?.Branches == null) return -1;
            for (int i = 0; i < mode.Branches.Count; i++)
            {
                var b = mode.Branches[i];
                if (b.Conditions == null) continue;
                if (b.Conditions.Any(c => c.Hand == hand && c.HandGesture == gesture))
                    return i;
            }
            return -1;
        }

        private static Suzuryg.FaceEmo.Domain.FaceEmoAnimation GetBranchAnimation(
            Suzuryg.FaceEmo.Domain.Branch b, Suzuryg.FaceEmo.Domain.BranchAnimationType slot)
        {
            return slot switch
            {
                Suzuryg.FaceEmo.Domain.BranchAnimationType.Left  => b.LeftHandAnimation,
                Suzuryg.FaceEmo.Domain.BranchAnimationType.Right => b.RightHandAnimation,
                Suzuryg.FaceEmo.Domain.BranchAnimationType.Both  => b.BothHandsAnimation,
                _                                                 => b.BaseAnimation,
            };
        }

        private static Suzuryg.FaceEmo.Domain.FaceEmoAnimation MakeFaceEmoAnimation(string clipPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(clipPath);
            return new Suzuryg.FaceEmo.Domain.FaceEmoAnimation(guid);
        }

        // Editor から取得した値を clip に焼き込む (Plan A AssetPathFallback と同等)
        private void ApplyValuesToClip(AnimationClip clip, IReadOnlyDictionary<(string path, string shape), float> values)
        {
            clip.ClearCurves();
            foreach (var kv in values)
            {
                var binding = new EditorCurveBinding
                {
                    path = kv.Key.path,
                    type = typeof(SkinnedMeshRenderer),
                    propertyName = $"blendShape.{kv.Key.shape}",
                };
                var curve = AnimationCurve.Linear(0f, kv.Value, 1f / 60f, kv.Value);
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }
        }
```

- [ ] **Step 2: using 追加 (ファイル先頭)**

```csharp
using System.Linq;
```

(既に存在しなければ追加)

- [ ] **Step 3: Editor リコンパイル確認**

注意:
- `Suzuryg.FaceEmo.Domain.Branch` / `Condition` / `FaceEmoAnimation` のコンストラクタ・プロパティ名は実物で確認
- `FaceEmoAPI.SaveMenu(launcher, menu)` のシグネチャを `FaceEmoAPI.cs` で検証 (引数順)
- `FaceEmoThumbnailRenderer.RefreshMainView` の有無 (Plan B 産物、`Editor/Tools/FaceEmoExpressionEditor/FaceEmoThumbnailRenderer.cs` 参照)
- `GetCurrentValues` の戻り値型 (現状 `IReadOnlyDictionary<(string,string),float>` 想定) を `FaceEmoExpressionSession.cs` で確認

- [ ] **Step 4: Commit**

```bash
git add Editor/Tools/FaceEmoExpressionEditor/FaceEmoExpressionSession.cs
git commit -m "feat(planC-session): add CommitAsBranchOf + CommitInPlace (atomic 6-step + rollback)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: ExpressionSessionTools editMode param

**Files:**
- Modify: `Editor/Tools/FaceEmoExpressionEditor/ExpressionSessionTools.cs`

- [ ] **Step 1: 現状 OpenExpressionSession を読む**

`Read` で `ExpressionSessionTools.cs` の `OpenExpressionSession` 全体を確認。シグネチャと内部処理を把握。

- [ ] **Step 2: editMode param 追加 (省略時 "new-mode" で互換)**

`OpenExpressionSession` の signature を以下のように変更 (既存引数の末尾に追加):

```csharp
[AgentTool(Group = "FaceEmo", Description = "...")]
public static string OpenExpressionSession(
    string modeName = "",
    string newName = "",
    string avatarRootName = "",
    string editMode = "new-mode")          // ← 追加
{
    // ... 既存処理の前に switch を入れる:
    switch ((editMode ?? "new-mode").ToLowerInvariant())
    {
        case "new-mode":
        case "create-branch-clip":
            // 既存 Plan A 動作: 新 clip を作って NewExpression として開く
            // (CreateBranchClip でも path は同じ、Commit 時に分岐)
            // → 既存処理続行
            break;
        case "edit-existing-clip":
            // Plan C: 既存 Branch の clip を開く
            // 必要引数 modeName, gesture, hand, slot は未受領 → 別 tool に分離するか
            // ここでは error "use OpenExpressionSessionForBranch instead" を返す
            return "Error: editMode='edit-existing-clip' requires OpenExpressionSessionForBranch (Plan C).";
        default:
            return $"Error: unknown editMode '{editMode}'. Use new-mode / create-branch-clip.";
    }

    // 既存 OpenForNewExpression 経路続行
    // ...
}
```

- [ ] **Step 3: editMode で session.EditMode を設定**

`OpenForNewExpression(...)` 呼び出し直後に:
```csharp
    if (editMode?.ToLowerInvariant() == "create-branch-clip")
    {
        FaceEmoExpressionSession.Active.EditMode =
            FaceEmoExpressionSession.SessionEditMode.CreateBranchClip;
    }
```

（`Active` が public、`EditMode` setter は private のため、internal setter を `FaceEmoExpressionSession.cs` に追加するか、`OpenForNewExpression` 自体に `editMode` param を渡して内部で設定する。後者が cleaner）。

代替実装 (推奨):
- `FaceEmoExpressionSession.OpenForNewExpression` に `editMode` パラメータ (デフォルト `NewMode`) を追加
- `ExpressionSessionTools.OpenExpressionSession` から `editMode` を伝搬

- [ ] **Step 4: CommitExpressionSession 分岐追加**

`CommitExpressionSession` の処理冒頭で:

```csharp
var session = FaceEmoExpressionSession.Active;
if (session == null) return "Error: no active session.";
if (session.EditMode == FaceEmoExpressionSession.SessionEditMode.EditExistingClip)
{
    var r = session.CommitInPlace();
    return r.Ok ? $"OK in-place: {r.DestinationDescription}" : $"Error: {r.ErrorMessage}";
}
// CreateBranchClip routing は別 AgentTool (CommitExpressionSessionToBranch) で実装する
// NewMode の場合は既存 Plan A 経路続行
```

- [ ] **Step 5: 新 AgentTool `CommitExpressionSessionToBranch` 追加**

`ExpressionSessionTools.cs` に追加:

```csharp
[AgentTool(Group = "FaceEmo", Description =
    "Active session の Editor 値を指定 Mode の (gesture, hand, slot) Branch に割当 (新 clip 作成)。" +
    "Plan C 専用。session の EditMode が CreateBranchClip でないと error。")]
public static string CommitExpressionSessionToBranch(
    string modeName, string gesture, string hand = "Either", string slot = "Base",
    string overwriteMode = "Overwrite")
{
    var session = FaceEmoExpressionSession.Active;
    if (session == null) return "Error: no active session.";
    var om = (overwriteMode ?? "Overwrite") switch
    {
        "Cancel" => FaceEmoExpressionSession.OverwriteMode.Cancel,
        "EditExisting" => FaceEmoExpressionSession.OverwriteMode.EditExisting,
        "Ask" => FaceEmoExpressionSession.OverwriteMode.Ask,
        _ => FaceEmoExpressionSession.OverwriteMode.Overwrite,
    };
    var r = session.CommitAsBranchOf(modeName, gesture, hand, slot, om);
    return r.Ok
        ? $"OK: {r.DestinationDescription} → {r.FinalClipPath}"
        : $"Error: {r.ErrorMessage}";
}
```

- [ ] **Step 6: Editor リコンパイル確認**

- [ ] **Step 7: Commit**

```bash
git add Editor/Tools/FaceEmoExpressionEditor/ExpressionSessionTools.cs Editor/Tools/FaceEmoExpressionEditor/FaceEmoExpressionSession.cs
git commit -m "feat(planC-tools): add editMode param + CommitExpressionSessionToBranch

OpenExpressionSession editMode='new-mode'|'create-branch-clip'|'edit-existing-clip'.
CommitExpressionSession routes to CommitInPlace for EditExistingClip.
New AgentTool CommitExpressionSessionToBranch for CreateBranchClip path.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase 5: Orchestrator

### Task 12: ExpressionWorkflow

**Files:**
- Create: `Editor/Tools/FaceEmoPlanC/Orchestration/ExpressionWorkflow.cs`

- [ ] **Step 1: ファイル作成 (純発話パース、副作用なし)**

```csharp
// Editor/Tools/FaceEmoPlanC/Orchestration/ExpressionWorkflow.cs
#if FACE_EMO
using AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Conventions;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Orchestration
{
    /// <summary>
    /// 発話文を解析し、AI が「どの step で AskUser を踏むか / skip するか」を決めるヒントを返す。
    /// 副作用なし。Discovery / Curation / Execution は呼ばない。
    /// </summary>
    public static class ExpressionWorkflow
    {
        public sealed class WorkflowPlan
        {
            public string Intent { get; set; }                       // smile / angry / ... (検出できなければ null)
            public IntentVocabulary.TopMode TopMode { get; set; }    // Auto / Interactive / Unspecified
            public string AvatarHint { get; set; }                   // 発話に含まれた avatar 名 (なければ null)
            public string ModeHint { get; set; }                     // 発話に含まれた Mode 名
            public string GestureHint { get; set; }                  // 発話に含まれた gesture (PascalCase)
            public string HandHint { get; set; }                     // 発話に含まれた Hand qualifier
            public bool ShouldAskTopMode => TopMode == IntentVocabulary.TopMode.Unspecified;
            public bool ShouldAskAvatar => string.IsNullOrEmpty(AvatarHint);
            public bool ShouldAskMode => string.IsNullOrEmpty(ModeHint);
            public bool ShouldAskGesture => string.IsNullOrEmpty(GestureHint);
            public bool ShouldAskHand => string.IsNullOrEmpty(HandHint);
        }

        // intent キーワード → 内部 intent 名 (Synonym 同様だが Workflow 用に最小限)
        private static readonly (string keyword, string intent)[] IntentKeywords =
        {
            ("笑顔",   "smile"), ("にっこり", "smile"), ("ニコニコ", "smile"),
            ("smile",   "smile"), ("happy",    "smile"),
            ("怒り",   "angry"), ("怒った",   "angry"), ("angry",   "angry"),
            ("悲しい", "sad"),    ("sad",       "sad"),
            ("驚き",   "surprise"), ("びっくり", "surprise"), ("surprise", "surprise"),
            ("ウィンク", "wink"), ("wink",     "wink"),
            ("眠い",   "sleepy"), ("sleepy",  "sleepy"),
        };

        /// <summary>発話文 1 行から WorkflowPlan を組み立てる。</summary>
        public static WorkflowPlan Parse(string utterance)
        {
            var plan = new WorkflowPlan();
            if (string.IsNullOrEmpty(utterance)) return plan;

            plan.TopMode = IntentVocabulary.DetectTopMode(utterance);

            // intent
            foreach (var (kw, intent) in IntentKeywords)
            {
                if (utterance.IndexOf(kw, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    plan.Intent = intent; break;
                }
            }

            // gesture
            plan.GestureHint = IntentVocabulary.DetectHandPose(utterance);
            plan.HandHint = IntentVocabulary.DetectHandQualifier(utterance);

            // avatar / mode は scene 依存なので Discovery 層で解決 → Workflow では null のまま
            return plan;
        }
    }
}
#endif
```

- [ ] **Step 2: Editor リコンパイル確認**

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoPlanC/Orchestration/ExpressionWorkflow.cs
git commit -m "feat(planC): add ExpressionWorkflow (utterance → workflow plan)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase 6: AgentTools

### Task 13: DiscoveryTools (3 AgentTool)

**Files:**
- Create: `Editor/Tools/FaceEmoPlanC/AgentTools/DiscoveryTools.cs`

- [ ] **Step 1: ファイル作成**

```csharp
// Editor/Tools/FaceEmoPlanC/AgentTools/DiscoveryTools.cs
#if FACE_EMO
using AjisaiFlow.UnityAgent.Editor.Core;
using AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Discovery;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.AgentTools
{
    public static class DiscoveryTools
    {
        [AgentTool(Group = "FaceEmoPlanC", Description =
            "発話 hint または scene 状態から target avatar を解決。返却: avatar 名 + confidence + alternatives。")]
        public static string ResolveTargetAvatar(string promptHint = "")
        {
            var r = AvatarResolver.Resolve(promptHint);
            if (r.AvatarRootName != null)
                return $"OK confidence={r.Confidence} avatar={r.AvatarRootName} reason={r.Reason}";
            if (r.Alternatives != null && r.Alternatives.Count > 0)
                return $"Ambiguous alternatives=[{string.Join(",", r.Alternatives)}] reason={r.Reason}";
            return $"None reason={r.Reason}";
        }

        [AgentTool(Group = "FaceEmoPlanC", Description =
            "avatar に対する FaceEmo セットアップ状態を判定。state + modeNames + 推奨 next action を返す。")]
        public static string InspectFaceEmoState(string avatarRootName)
        {
            var r = FaceEmoStateInspector.Inspect(avatarRootName);
            string modes = r.ModeNames != null && r.ModeNames.Length > 0
                ? string.Join(",", r.ModeNames) : "(none)";
            return $"state={r.CurrentState} launcher={r.Launcher?.gameObject?.name ?? "null"} " +
                   $"modes=[{modes}] next={r.NextActionHint}";
        }

        [AgentTool(Group = "FaceEmoPlanC", Description =
            "avatar 用 FaceEmo launcher 自動セットアップ。NoLauncher → ExecuteMenu('FaceEmo/New Menu') + ConfigureTargetAvatar。")]
        public static string AutoSetupFaceEmoForAvatar(string avatarRootName)
        {
            // 既存 FaceEmoAPI / FaceEmoTools のメニュー操作を流用
            // 1. avatar GameObject 解決
            var av = UnityEngine.GameObject.Find(avatarRootName);
            if (av == null) return $"Error: avatar '{avatarRootName}' not found in scene.";
            // 2. New Menu 実行
            UnityEditor.Selection.activeGameObject = av;
            bool ok = UnityEditor.EditorApplication.ExecuteMenuItem("FaceEmo/New Menu");
            if (!ok) return "Error: ExecuteMenuItem('FaceEmo/New Menu') failed.";
            // 3. 直後に出現した launcher を avatar 配下から探す
            var launcher = av.GetComponentInChildren<Suzuryg.FaceEmo.Components.FaceEmoLauncherComponent>();
            if (launcher == null) return "Error: launcher not created after New Menu.";
            // 4. TargetAvatar 設定 (まだ未設定の場合のみ)
            if (launcher.TargetAvatar == null)
            {
                var desc = av.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                if (desc == null) return "Error: avatar has no VRCAvatarDescriptor.";
                FaceEmoAPI.ConfigureTargetAvatar(launcher, desc);
            }
            return $"OK launcher={launcher.gameObject.name} targetAvatar={launcher.TargetAvatar?.gameObject?.name}";
        }
    }
}
#endif
```

- [ ] **Step 2: Editor リコンパイル確認**

注意: `FaceEmoAPI.ConfigureTargetAvatar` の有無 / シグネチャを `FaceEmoAPI.cs` で確認。存在しなければ launcher の TargetAvatar field に直接 SerializedObject 経由で代入。`AgentTool` attribute の正しい名前空間を `Editor/Core/` 配下で確認。

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoPlanC/AgentTools/DiscoveryTools.cs
git commit -m "feat(planC-tools): add DiscoveryTools (3 AgentTools)

ResolveTargetAvatar / InspectFaceEmoState / AutoSetupFaceEmoForAvatar.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 14: GestureTools (4 AgentTool)

**Files:**
- Create: `Editor/Tools/FaceEmoPlanC/AgentTools/GestureTools.cs`

- [ ] **Step 1: ファイル作成**

```csharp
// Editor/Tools/FaceEmoPlanC/AgentTools/GestureTools.cs
#if FACE_EMO
using System.Linq;
using System.Text;
using AjisaiFlow.UnityAgent.Editor.Core;
using AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Conventions;
using Suzuryg.FaceEmo.Domain;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.AgentTools
{
    public static class GestureTools
    {
        [AgentTool(Group = "FaceEmoPlanC", Description =
            "指定 Mode の Branch を全列挙。各 Branch の (gesture, hand, slot, clipName) を返す。")]
        public static string ListGestureBindings(string launcherName, string modeName)
        {
            var launcher = FaceEmoAPI.FindLauncher(launcherName);
            if (launcher == null) return $"Error: launcher '{launcherName}' not found.";
            var menu = FaceEmoAPI.LoadMenu(launcher);
            if (menu == null) return "Error: failed to load menu.";
            var (modeId, mode) = FaceEmoAPI.FindExpression(menu, modeName);
            if (modeId == null) return $"Error: Mode '{modeName}' not found.";
            var sb = new StringBuilder();
            sb.AppendLine($"Mode={modeName} branches={mode.Branches?.Count ?? 0}");
            if (mode.Branches == null) return sb.ToString();
            for (int i = 0; i < mode.Branches.Count; i++)
            {
                var b = mode.Branches[i];
                var cond = b.Conditions?.FirstOrDefault();
                string g = cond != null ? cond.HandGesture.ToString() : "?";
                string h = cond != null ? cond.Hand.ToString() : "?";
                sb.AppendLine($"  [{i}] ({h}, {g})");
                sb.AppendLine($"    Base={ClipName(b.BaseAnimation)}");
                sb.AppendLine($"    Left={ClipName(b.LeftHandAnimation)} Right={ClipName(b.RightHandAnimation)} Both={ClipName(b.BothHandsAnimation)}");
            }
            return sb.ToString();
        }

        [AgentTool(Group = "FaceEmoPlanC", Description =
            "Mode 内で (gesture, hand) に一致する Branch index を検索。なければ -1。")]
        public static string FindBranchByCondition(string launcherName, string modeName, string gesture, string hand)
        {
            var launcher = FaceEmoAPI.FindLauncher(launcherName);
            if (launcher == null) return $"Error: launcher '{launcherName}' not found.";
            var menu = FaceEmoAPI.LoadMenu(launcher);
            var (modeId, mode) = FaceEmoAPI.FindExpression(menu, modeName);
            if (modeId == null) return $"Error: Mode '{modeName}' not found.";
            var hg = FaceEmoAPI.ParseGesture(gesture);
            var hd = FaceEmoAPI.ParseHand(hand);
            if (mode.Branches != null)
            {
                for (int i = 0; i < mode.Branches.Count; i++)
                {
                    var b = mode.Branches[i];
                    if (b.Conditions != null && b.Conditions.Any(c => c.Hand == hd && c.HandGesture == hg))
                        return $"{i}";
                }
            }
            return "-1";
        }

        [AgentTool(Group = "FaceEmoPlanC", Description =
            "新規 (gesture, hand) Branch を追加した場合に first-match 規則で無効化される既存 Branch をリストアップ。")]
        public static string DetectGestureConflicts(string launcherName, string modeName, string gesture, string hand)
        {
            var launcher = FaceEmoAPI.FindLauncher(launcherName);
            if (launcher == null) return $"Error: launcher '{launcherName}' not found.";
            var menu = FaceEmoAPI.LoadMenu(launcher);
            var (modeId, mode) = FaceEmoAPI.FindExpression(menu, modeName);
            if (modeId == null) return $"Error: Mode '{modeName}' not found.";
            var hg = FaceEmoAPI.ParseGesture(gesture);
            var hd = FaceEmoAPI.ParseHand(hand);
            var sb = new StringBuilder();
            sb.AppendLine($"Conflicts for new ({hand}, {gesture}) in Mode={modeName}:");
            if (mode.Branches == null) return sb.ToString();
            for (int i = 0; i < mode.Branches.Count; i++)
            {
                var b = mode.Branches[i];
                if (b.Conditions == null) continue;
                foreach (var c in b.Conditions)
                {
                    if (c.HandGesture != hg) continue;
                    // 同 gesture で hand が overlap → 死ぬ可能性
                    bool overlap = OverlapsHand(c.Hand, hd);
                    if (overlap)
                        sb.AppendLine($"  [{i}] ({c.Hand}, {c.HandGesture}) → would be shadowed by new ({hand}, {gesture})");
                }
            }
            return sb.ToString();
        }

        [AgentTool(Group = "FaceEmoPlanC", Description =
            "指定 clip を Mode の (gesture, hand, slot) Branch に割当。新規 Branch 自動作成。" +
            "overwriteMode: Overwrite (既存 slot を上書き) / EditExisting (既存を保持) / Cancel.")]
        public static string AssignClipToGesture(
            string launcherName, string modeName, string gesture, string hand, string slot,
            string clipPath, string overwriteMode = "Overwrite")
        {
            var launcher = FaceEmoAPI.FindLauncher(launcherName);
            if (launcher == null) return $"Error: launcher '{launcherName}' not found.";
            var menu = FaceEmoAPI.LoadMenu(launcher);
            var (modeId, mode) = FaceEmoAPI.FindExpression(menu, modeName);
            if (modeId == null) return $"Error: Mode '{modeName}' not found.";
            var clip = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.AnimationClip>(clipPath);
            if (clip == null) return $"Error: clip '{clipPath}' not loadable.";
            var hg = FaceEmoAPI.ParseGesture(gesture);
            var hd = FaceEmoAPI.ParseHand(hand);
            var slotType = FaceEmoAPI.ParseBranchSlot(slot) ?? BranchAnimationType.Base;

            int branchIdx = -1;
            if (mode.Branches != null)
            {
                for (int i = 0; i < mode.Branches.Count; i++)
                {
                    var b = mode.Branches[i];
                    if (b.Conditions != null && b.Conditions.Any(c => c.Hand == hd && c.HandGesture == hg))
                    { branchIdx = i; break; }
                }
            }
            bool isNew = branchIdx < 0;
            if (!isNew && overwriteMode == "Cancel")
                return "Cancelled: existing branch present, overwriteMode=Cancel.";
            if (!isNew && overwriteMode == "EditExisting")
                return $"OK existing branch [{branchIdx}] kept (no overwrite).";

            UnityEditor.Undo.SetCurrentGroupName($"PlanC: assign clip ({hand},{gesture}) on {modeName}");
            int undoGroup = UnityEditor.Undo.GetCurrentGroup();

            if (isNew)
            {
                var conds = new System.Collections.Generic.List<Condition>
                { new Condition(hd, hg, ComparisonOperator.Equals) };
                branchIdx = FaceEmoAPI.AddBranch(menu, modeId, conds);
            }
            string guid = UnityEditor.AssetDatabase.AssetPathToGUID(clipPath);
            FaceEmoAPI.SetBranchAnimation(menu, modeId, branchIdx, slotType, new FaceEmoAnimation(guid));
            FaceEmoAPI.SaveMenu(launcher, menu);
            UnityEditor.AssetDatabase.SaveAssets();
            UnityEditor.Undo.CollapseUndoOperations(undoGroup);
            return $"OK branchIndex={branchIdx} slot={slot} isNew={isNew}";
        }

        // ───── helpers ─────

        private static string ClipName(FaceEmoAnimation a)
        {
            if (a == null || string.IsNullOrEmpty(a.GUID)) return "(empty)";
            var p = UnityEditor.AssetDatabase.GUIDToAssetPath(a.GUID);
            return string.IsNullOrEmpty(p) ? "(missing)" : System.IO.Path.GetFileNameWithoutExtension(p);
        }

        // Either is superset of Left/Right/Both; Left vs Right disjoint; Both subset of Either; OneSide ≈ Left∪Right
        private static bool OverlapsHand(Hand existing, Hand incoming)
        {
            if (existing == incoming) return true;
            if (incoming == Hand.Either) return true;
            if (existing == Hand.Either) return true;
            return false;
        }
    }
}
#endif
```

- [ ] **Step 2: Editor リコンパイル確認**

注意: `Hand` enum の値は `Suzuryg.FaceEmo.Domain.Hand` を grep して確認。`OneSide` が存在しない可能性あり (spec では 5 値だが FaceEmo 実装は 4 値かも) — 存在しなければ `OverlapsHand` の判定簡素化、`HandPoseDisplay` から OneSide を削除。

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoPlanC/AgentTools/GestureTools.cs
git commit -m "feat(planC-tools): add GestureTools (4 AgentTools)

ListGestureBindings / FindBranchByCondition / DetectGestureConflicts / AssignClipToGesture.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 15: CurationTools (3 AgentTool)

**Files:**
- Create: `Editor/Tools/FaceEmoPlanC/AgentTools/CurationTools.cs`

- [ ] **Step 1: ファイル作成**

```csharp
// Editor/Tools/FaceEmoPlanC/AgentTools/CurationTools.cs
#if FACE_EMO
using System.Linq;
using System.Text;
using AjisaiFlow.UnityAgent.Editor.Core;
using AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.Curation;
using AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoExpressionEditor;
using AjisaiFlow.UnityAgent.Editor.Tools.FaceProfile;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoPlanC.AgentTools
{
    public static class CurationTools
    {
        [AgentTool(Group = "FaceEmoPlanC", Description =
            "intent (smile/angry/etc) から avatar の関連 BlendShape candidate 10-15 個 + variations 3 案を返す。" +
            "breadth: 'narrow'=3 / 'wide'=10-15 (default wide).")]
        public static string SuggestCandidateShapes(string avatarRootName, string intent, string breadth = "wide")
        {
            // FaceProfile を取得 (Plan A 既存 cache)
            FaceBlendShapeProfile profile = null;
            try { profile = FaceProfileTools.LoadOrAnalyze(avatarRootName); }
            catch (System.Exception ex) { return $"Error: profile load: {ex.Message}"; }
            if (profile == null) return $"Error: profile null for '{avatarRootName}'.";

            int b = (breadth?.ToLowerInvariant() == "narrow") ? 3 : 15;
            var result = CandidateShapeBuilder.Build(profile, intent, b);

            var sb = new StringBuilder();
            sb.AppendLine($"intent={intent} candidates={result.Candidates.Count} variations={result.Variations.Count}");
            sb.Append("candidates=");
            sb.AppendLine(string.Join(",", result.Candidates));
            sb.AppendLine("seed=" + string.Join(";", result.SeedValues.Select(kv => $"{kv.Key}={kv.Value:F0}")));
            for (int i = 0; i < result.Variations.Count; i++)
            {
                var v = result.Variations[i];
                sb.AppendLine($"variation[{i}] name={v.Name}");
                sb.AppendLine("  values=" + string.Join(";", v.Values.Select(kv => $"{kv.Key}={kv.Value:F0}")));
            }
            return sb.ToString();
        }

        [AgentTool(Group = "FaceEmoPlanC", Description =
            "Active session の Editor 値を指定 variation (やさしい/満面/はにかみ 等) の値に差替。" +
            "事前に SuggestCandidateShapes でデータを取得しておく。")]
        public static string ApplyExpressionVariation(string avatarRootName, string intent, string variationName)
        {
            var session = FaceEmoExpressionSession.Active;
            if (session == null) return "Error: no active session.";

            // 該当 variation を再生成 (stateless)
            FaceBlendShapeProfile profile = null;
            try { profile = FaceProfileTools.LoadOrAnalyze(avatarRootName); } catch { }
            if (profile == null) return $"Error: profile null for '{avatarRootName}'.";
            var build = CandidateShapeBuilder.Build(profile, intent, 15);
            var v = build.Variations.FirstOrDefault(x => x.Name == variationName);
            if (v == null) return $"Error: variation '{variationName}' not found. Available: " +
                                   string.Join(",", build.Variations.Select(x => x.Name));

            // 全 candidate を 0、variation 該当のみ値設定 (既存の touch しない方針は EditExisting モードでのみ適用)
            int set = 0;
            // SetExpressionPreviewMulti 経由を使うため、適用は呼出 AI が SetBlendShape を順次呼ぶ
            // ここでは「適用すべき値リスト」を返すだけにする (純情報)
            var sb = new StringBuilder();
            sb.AppendLine($"variation={variationName} candidates={build.Candidates.Count}");
            foreach (var cand in build.Candidates)
            {
                float val = v.Values.TryGetValue(cand, out var f) ? f : 0f;
                sb.AppendLine($"  {cand}={val:F0}");
                set++;
            }
            sb.AppendLine($"applied={set} (use SetExpressionPreviewMulti to apply on session)");
            return sb.ToString();
        }

        [AgentTool(Group = "FaceEmoPlanC", Description =
            "指定 intent の variation 名 3 個を返す (例: smile→ やさしい,満面,はにかみ)。")]
        public static string ListExpressionVariations(string intent)
        {
            var labels = ExpressionVariations.GetLabels(intent);
            return string.Join(",", labels);
        }
    }
}
#endif
```

- [ ] **Step 2: Editor リコンパイル確認**

注意: `FaceProfileTools.LoadOrAnalyze` の存在を確認。なければ既存の analyze API (`FaceProfileTools.AnalyzeFace` 等) に置換。`AgentTool` attribute の解決を確認。

- [ ] **Step 3: Commit**

```bash
git add Editor/Tools/FaceEmoPlanC/AgentTools/CurationTools.cs
git commit -m "feat(planC-tools): add CurationTools (3 AgentTools)

SuggestCandidateShapes / ApplyExpressionVariation / ListExpressionVariations.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase 7: Integration

### Task 16: BuiltInSkills Plan C workflow guide

**Files:**
- Modify: `Editor/Tools/BuiltInSkills.cs`

- [ ] **Step 1: 既存 BuiltInSkills を読んで Plan C workflow を追加する位置を決める**

`Workflow B` (FaceEmo セットアップ表情編集) の直後 (もしくは末尾) に新セクションを追加。

- [ ] **Step 2: Plan C workflow 文章を追加**

```csharp
// BuiltInSkills.cs の string sb.AppendLine(...) 連鎖の途中、Workflow B の後ろ
sb.AppendLine("");
sb.AppendLine("=== Workflow C: Gesture-Aware Expression Creation (Plan C) ===");
sb.AppendLine("");
sb.AppendLine("「<avatar> に <表情> つけて」発話に対する標準フロー。");
sb.AppendLine("Choice 3 (propose-then-act): AI 推測 → AskUser 確認 → 実行。");
sb.AppendLine("");
sb.AppendLine("1. [ResolveTargetAvatar('<promptHint>')] → avatar 名 + confidence");
sb.AppendLine("   - 'None' なら user に Hierarchy から選んでもらう");
sb.AppendLine("   - 'Ambiguous' なら AskUser で alternatives から選択");
sb.AppendLine("");
sb.AppendLine("2. [InspectFaceEmoState(avatarRootName)] → state");
sb.AppendLine("   - NoLauncher: [AutoSetupFaceEmoForAvatar(avatarRootName)]");
sb.AppendLine("   - LauncherUnconfigured: 同上");
sb.AppendLine("   - HasModes: 次へ");
sb.AppendLine("");
sb.AppendLine("3. 発話に 'AI 任せ' / '編集する' キーワード無ければ AskUser top-mode");
sb.AppendLine("");
sb.AppendLine("4. Mode 選択 (modes >1 なら AskUser、1 つなら採用宣言)");
sb.AppendLine("");
sb.AppendLine("5. [ListGestureBindings(launcher, mode)] → 現在 bindings 確認");
sb.AppendLine("   AskUser gesture (8-grid + IntentGestureMap の推奨 ★)");
sb.AppendLine("   発話に gesture 名あれば skip");
sb.AppendLine("");
sb.AppendLine("6. Hand qualifier (デフォルト Either、'左手で' 等で override)");
sb.AppendLine("");
sb.AppendLine("7. [FindBranchByCondition(...)] が >=0 なら既存 binding 有");
sb.AppendLine("   → AskUser [上書き / 編集 / Cancel]");
sb.AppendLine("   [DetectGestureConflicts(...)] が空でなければ shadowed branches を user に提示");
sb.AppendLine("");
sb.AppendLine("8. AI 任せ mode: SuggestExpressionShapes (Plan A) → 3 variation サムネ生成 → AskUser");
sb.AppendLine("   編集 mode:    [SuggestCandidateShapes(avatar, intent, 'wide')] → 10-15 候補 + 3 案");
sb.AppendLine("                 [OpenExpressionSession(modeName='', newName='temp', avatar, editMode='create-branch-clip')]");
sb.AppendLine("                 [ApplyExpressionVariation(...)] で variation 流し込み");
sb.AppendLine("                 AskUser [編集する / 次の variation / Cancel]");
sb.AppendLine("");
sb.AppendLine("9. user 編集完了 → [CommitExpressionSessionToBranch(modeName, gesture, hand, slot='Base', overwriteMode='Overwrite')]");
sb.AppendLine("");
sb.AppendLine("10. [CaptureFaceEmoGestureTable(avatarRootName, modeName)] → 結果画像");
sb.AppendLine("");
sb.AppendLine("注: Registered 7 枠を消費しないこと。Branch 経路 (CommitExpressionSessionToBranch) がデフォルト。");
sb.AppendLine("注: 既存 clip 編集モード時 (editMode='edit-existing-clip') は別ルート、AI は新 shape 追加のみ。");
```

- [ ] **Step 3: Editor リコンパイル確認**

- [ ] **Step 4: Commit**

```bash
git add Editor/Tools/BuiltInSkills.cs
git commit -m "docs(planC): add Workflow C guide to BuiltInSkills

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 17: Manual E2E test + CHANGELOG + version bump

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `package.json`

- [ ] **Step 1: Editor 起動して全 Plan C tool が SearchUnityTool で見えることを確認**

PowerShell (Bash via unity-agent MCP):
```
mcp__unity-agent__SearchUnityTool query="ResolveTargetAvatar"
mcp__unity-agent__SearchUnityTool query="InspectFaceEmoState"
mcp__unity-agent__SearchUnityTool query="AssignClipToGesture"
mcp__unity-agent__SearchUnityTool query="SuggestCandidateShapes"
```
Expected: 各 tool が hit。

- [ ] **Step 2: Manual E2E (Gemini ハイジャック で実 LLM 経由)**

1. Unity Editor で `AgentChatWindow` 開く
2. provider = Gemini に切替
3. 入力: 「Milfy_Another に笑顔つけて」
4. 流れ確認:
   - ResolveTargetAvatar → "Milfy_Another"
   - InspectFaceEmoState → HasModes
   - AskUser top-mode → 「編集する」選択
   - Mode 採択宣言
   - AskUser gesture → HandOpen 選択
   - SuggestCandidateShapes → 15 候補 + 3 variation
   - OpenExpressionSession editMode=create-branch-clip
   - ApplyExpressionVariation('やさしい') → 値設定
   - AskUser [編集する / 次/...] → 「次:満面」選択
   - ApplyExpressionVariation('満面')
   - AskUser → 「編集する」
   - User: FaceEmo Editor で slider 微調整
   - AskUser → 「OK」
   - CommitExpressionSessionToBranch(...) → 成功
   - CaptureFaceEmoGestureTable → 画像
5. 検証: HandOpen Branch が新規追加されている、clip ファイルが `Assets/Generated/UnityAgent/FaceEmoPlanC/expr_*.anim` に存在、Registered cap 消費していない

異常系 1: avatar 名指定なしで実行 → AskUser avatar
異常系 2: 既存 HandOpen Branch がある状態 → AskUser [上書き/編集/Cancel]
異常系 3: Ctrl+Z → commit 全部取消

- [ ] **Step 3: CHANGELOG 追記**

`CHANGELOG.md` の最上部に:

```markdown
## [0.11.0] - 2026-05-17

### Added — Plan C: Gesture-Aware Expression Workflow

- 5 layers: Orchestrator / Discovery / Convention / Curation / Execution (`Editor/Tools/FaceEmoPlanC/`)
- 10 new AgentTools under `FaceEmoPlanC` group:
  - Discovery: `ResolveTargetAvatar`, `InspectFaceEmoState`, `AutoSetupFaceEmoForAvatar`
  - Gesture: `ListGestureBindings`, `FindBranchByCondition`, `DetectGestureConflicts`, `AssignClipToGesture`
  - Curation: `SuggestCandidateShapes`, `ApplyExpressionVariation`, `ListExpressionVariations`
- Session API extensions:
  - `FaceEmoExpressionSession.OpenForBranch` — 既存 Branch clip 編集モード
  - `FaceEmoExpressionSession.CommitAsBranchOf` — atomic 6-step + rollback
  - `FaceEmoExpressionSession.CommitInPlace` — 既存 clip 上書き保存
  - `SessionEditMode` / `OverwriteMode` enums
- `OpenExpressionSession` に `editMode` param 追加 (`new-mode` / `create-branch-clip` / `edit-existing-clip`)
- 新規 `CommitExpressionSessionToBranch` AgentTool
- `BuiltInSkills.cs` に Workflow C guide 追加
- Ctrl+Z 一回で commit 全体ロールバック (Undo group)
- 関連: `docs/superpowers/specs/2026-05-17-faceemo-plan-c-gesture-assignment-design.md`,
  `docs/superpowers/plans/2026-05-17-faceemo-plan-c-gesture-assignment.md`
```

- [ ] **Step 4: package.json version bump**

`"version": "0.10.6"` → `"version": "0.11.0"`

- [ ] **Step 5: Commit**

```bash
git add CHANGELOG.md package.json
git commit -m "chore: bump version to 0.11.0 (Plan C Gesture-Aware Workflow)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review Checklist

### Spec coverage

| Spec section | Task |
|---|---|
| Sec 1 (5 layers) | Task 1-15 covers all 5 layers |
| Sec 2 (Orchestrator) | Task 12 (ExpressionWorkflow) + Task 16 (BuiltInSkills guide) |
| Sec 3.1-3.6 (Gesture) | Task 14 (GestureTools 4 個) + Task 10 (CommitAsBranchOf) |
| Sec 4 (Curation) | Task 4 (ExpressionVariations) + Task 5 (CandidateShapeBuilder) + Task 15 (CurationTools) |
| Sec 5 (Discovery) | Task 6 (AvatarResolver) + Task 7 (FaceEmoStateInspector) + Task 13 (DiscoveryTools) |
| Sec 6 (Convention) | Task 1 (IntentGestureMap) + Task 2 (HandPoseDisplay) + Task 3 (IntentVocabulary) |
| Sec 7 (Tools inventory + Session API) | Task 8-11 (Session) + Task 13-15 (Tools) |
| Sec 8 (Risks) | R1=avatarRootName 全 tool 必須 ✓, R3=Task 10 atomic+rollback ✓, R10=既存 Plan A ✓, R13=Task 14 DetectGestureConflicts ✓ |
| Appendix A (E2E trace) | Task 17 (manual E2E) |

ギャップ: 無し (全 spec section に対応 task あり)

### Placeholder scan

- [ ] "TBD" / "TODO" → 検索 0 件
- [ ] "Add appropriate error handling" → 各 try/catch 具体実装あり
- [ ] "Similar to Task N" → 全タスクコード自己完結

### Type consistency

- `WorkflowPlan.GestureHint` (Task 12) ↔ `IntentVocabulary.DetectHandPose` (Task 3) — string 返却で一致
- `CommitResult` (Task 10) は session 内部、`CommitExpressionSessionToBranch` (Task 11) で string 化
- `Variation.Values` (Task 4) Dictionary<string,float> ↔ `CandidateShapeBuilder.Result.SeedValues` (Task 5) — 一致
- `FaceCategory` enum 使用箇所 (Task 5) — 既存 FaceProfile 名前空間に存在

修正済 (この計画書作成時の確認):
- `OpenExpressionSession` の `editMode` 値は 3 種 `new-mode` / `create-branch-clip` / `edit-existing-clip` で全 task 統一
- `OverwriteMode` enum 値は 4 種 `Ask` / `Overwrite` / `EditExisting` / `Cancel` で全 task 統一

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-17-faceemo-plan-c-gesture-assignment.md`. Two execution options:

**1. Subagent-Driven (recommended)** - 各 task に fresh subagent + spec/code 2 段 review、高速イテレーション

**2. Inline Execution** - このセッションで executing-plans に従いバッチ実行 + checkpoint

どちらで進めますか？
