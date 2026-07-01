using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace AjisaiFlow.UnityAgent.Editor.UI
{
    /// <summary>
    /// UI Toolkit の VisualElement は 1 要素あたり 65535 頂点を超えるメッシュを描画できない
    /// (テキストは可視グリフ 1 個 ≒ 4 頂点)。長文を単一 Label に流し込むと上限を超え、
    /// 要素まるごと非表示 (レイアウト高さだけ残る空白) になり
    /// "A VisualElement must not allocate more than 65535 vertices" が出る。
    /// これを避けるため、テキストを安全な文字数で複数 Label に分割する。
    /// </summary>
    internal static class LongText
    {
        // 65535 / 4 ≒ 16383 グリフが理論上限。実測では 15800 文字の結果が消えており、
        // 選択ハイライト・下線・CJK 等の追加頂点で 1 文字あたり 4 頂点を超えることがある。
        // 十分な余裕 (上限の約半分) を見て保守的に設定する。
        internal const int MaxCharsPerLabel = 8000;

        /// <summary>
        /// text が閾値以下なら単一 Label を、超える場合は改行境界で分割した
        /// 複数 Label を内包する Column を返す。configure で各 Label を装飾する。
        /// </summary>
        public static VisualElement Build(string text, Action<Label> configure)
        {
            text = text ?? "";
            if (text.Length <= MaxCharsPerLabel)
            {
                var label = new Label(text);
                configure?.Invoke(label);
                return label;
            }

            var column = new VisualElement();
            column.style.flexDirection = FlexDirection.Column;
            foreach (var chunk in SplitChunks(text, MaxCharsPerLabel))
            {
                var label = new Label(chunk);
                configure?.Invoke(label);
                column.Add(label);
            }
            return column;
        }

        /// <summary>
        /// text を max 文字以下のチャンクに分割する。可能な限り改行境界で切り、
        /// rich text の行内タグ (bold/color 等) を途中で割らないようにする。
        /// </summary>
        public static IEnumerable<string> SplitChunks(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            int i = 0;
            int n = text.Length;
            while (i < n)
            {
                int remaining = n - i;
                if (remaining <= max)
                {
                    yield return text.Substring(i);
                    yield break;
                }

                int limit = i + max;                     // 排他的上限
                // [i, limit) 内で最後の改行を探し、その直後を境界にする
                int nl = text.LastIndexOf('\n', limit - 1, max);
                int end = (nl > i) ? nl + 1 : limit;      // 改行が無ければハード分割
                yield return text.Substring(i, end - i);
                i = end;
            }
        }
    }

    /// <summary>
    /// ストリーミング等でテキストが伸び続ける用途向けの Label ラッパー。
    /// 閾値以下では単一 Label を in-place 更新して選択状態を保持し (低コスト)、
    /// 超えたら複数 Label に分割し直して 65535 頂点上限を回避する。
    /// </summary>
    internal sealed class ChunkedLabel : VisualElement
    {
        readonly Action<Label> _configure;
        string _text = "";

        public ChunkedLabel(Action<Label> configure)
        {
            _configure = configure;
            style.flexDirection = FlexDirection.Column;
        }

        /// <summary>現在保持しているテキスト。</summary>
        public string Text => _text;

        /// <summary>テキストを差し替える。長さに応じて単一 / 分割を自動で切り替える。</summary>
        public void SetText(string text)
        {
            text = text ?? "";
            if (text == _text) return;
            _text = text;

            if (text.Length <= LongText.MaxCharsPerLabel)
            {
                // 高速パス: 単一 Label を再利用して text だけ差し替える (選択状態を保持)
                Label label = (childCount == 1) ? ElementAt(0) as Label : null;
                if (label == null)
                {
                    Clear();
                    label = NewLabel();
                    Add(label);
                }
                label.text = text;
                return;
            }

            // 分割パス (長文のみ): チャンクごとに Label を作り直す
            Clear();
            foreach (var chunk in LongText.SplitChunks(text, LongText.MaxCharsPerLabel))
            {
                var label = NewLabel();
                label.text = chunk;
                Add(label);
            }
        }

        Label NewLabel()
        {
            var label = new Label();
            _configure?.Invoke(label);
            return label;
        }
    }
}
