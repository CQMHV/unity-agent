using System.Collections.Generic;
using System.Diagnostics;
using AjisaiFlow.MD3SDK.Editor;
using UnityEngine;
using UnityEngine.UIElements;
using static AjisaiFlow.UnityAgent.Editor.L10n;

namespace AjisaiFlow.UnityAgent.Editor.UI
{
    /// <summary>チャット履歴一覧パネル。ファイルは遅延・分割ロードしてフリーズを防ぐ。</summary>
    internal class HistoryPanel : VisualElement
    {
        readonly MD3Theme _theme;
        readonly ScrollView _scrollView;
        readonly TextField _searchField;

        public System.Action<string> OnSessionSelected;
        public System.Action<string> OnSessionDeleted;

        // 解析済みヘッダーのメモリキャッシュ（filePath -> header）。
        readonly Dictionary<string, ChatSessionHeader> _headerCache =
            new Dictionary<string, ChatSessionHeader>();
        // filePath -> 行 VisualElement。非同期更新での差し替えに使う。
        readonly Dictionary<string, VisualElement> _rowsByPath =
            new Dictionary<string, VisualElement>();

        readonly Queue<string> _pendingFiles = new Queue<string>();
        IVisualElementScheduledItem _loadPump;
        string _currentFilter = "";

        public HistoryPanel(MD3Theme theme)
        {
            _theme = theme;
            style.flexGrow = 1;
            style.display = DisplayStyle.None;

            _searchField = new TextField();
            _searchField.style.marginLeft = 12;
            _searchField.style.marginRight = 12;
            _searchField.style.marginTop = 8;
            _searchField.style.marginBottom = 8;
            _searchField.RegisterValueChangedCallback(evt => FilterItems(evt.newValue));
            Add(_searchField);

            _scrollView = new ScrollView(ScrollViewMode.Vertical);
            _scrollView.style.flexGrow = 1;
            Add(_scrollView);
        }

        /// <summary>
        /// 履歴一覧の読み込みを開始する。ファイル一覧から即座に行を並べ（フリーズなし）、
        /// 各行のタイトル・メッセージ数は時間予算付きポンプで分割解析して埋める。
        /// </summary>
        public void BeginLoad()
        {
            _loadPump?.Pause();
            _loadPump = null;
            _pendingFiles.Clear();
            _scrollView.Clear();
            _rowsByPath.Clear();

            var files = ChatHistoryManager.ListSessionFiles();
            foreach (var file in files)
            {
                _headerCache.TryGetValue(file, out var cached);
                var row = BuildRow(file, cached);
                _scrollView.Add(row);
                _rowsByPath[file] = row;
                ApplyFilterToRow(row, _currentFilter);
                if (cached == null) _pendingFiles.Enqueue(file);
            }

            if (_pendingFiles.Count > 0)
                _loadPump = schedule.Execute(PumpLoad).Every(1);
        }

        /// <summary>時間予算（約 32ms）内でできるだけファイルを解析し、行を差し替える。</summary>
        void PumpLoad()
        {
            var sw = Stopwatch.StartNew();
            while (_pendingFiles.Count > 0 && sw.ElapsedMilliseconds < 32)
            {
                string file = _pendingFiles.Dequeue();
                var header = ChatHistoryManager.ReadSessionHeader(file);
                if (header == null)
                {
                    // 破損・読み取り不能ファイル: 失敗状態の行に差し替える（プレースホルダのまま固定しない）。
                    header = new ChatSessionHeader
                    {
                        title = M("(読み込み失敗)"),
                        timestamp = ChatHistoryManager.TimestampFromFileName(file),
                        filePath = file,
                        messageCount = 0
                    };
                }
                _headerCache[file] = header;
                ReplaceRow(file, header);
            }
            if (_pendingFiles.Count == 0)
            {
                _loadPump?.Pause();
                _loadPump = null;
            }
        }

        /// <summary>指定セッションを一覧から取り除く（削除後に呼ぶ。再解析しない）。</summary>
        public void RemoveSession(string filePath)
        {
            if (_rowsByPath.TryGetValue(filePath, out var row))
            {
                row.RemoveFromHierarchy();
                _rowsByPath.Remove(filePath);
            }
            _headerCache.Remove(filePath);
        }

        /// <summary>
        /// 1 セッション分の行を構築する。header が null の場合は読み込み中プレースホルダ。
        /// </summary>
        VisualElement BuildRow(string filePath, ChatSessionHeader header)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.name = "session-item";

            string title = header != null && !string.IsNullOrEmpty(header.title)
                ? header.title
                : M("(読み込み中…)");
            string timestamp = header != null && !string.IsNullOrEmpty(header.timestamp)
                ? header.timestamp
                : ChatHistoryManager.TimestampFromFileName(filePath);
            string supporting = header != null
                ? string.Format("{0} · {1} {2}", timestamp, header.messageCount, M("メッセージ"))
                : timestamp;

            var item = new MD3ListItem(title, supporting, MD3Icon.Mail);
            item.style.flexGrow = 1;
            item.RegisterCallback<ClickEvent>(evt => OnSessionSelected?.Invoke(filePath));
            row.Add(item);

            var deleteBtn = new MD3IconButton(
                MD3Icon.Delete, MD3IconButtonStyle.Standard, MD3IconButtonSize.Small);
            deleteBtn.tooltip = M("この履歴を削除");
            deleteBtn.style.opacity = 0.35f;
            deleteBtn.style.flexShrink = 0;
            deleteBtn.style.marginRight = 8;
            deleteBtn.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                string dlgTitle =
                    _headerCache.TryGetValue(filePath, out var ch) && !string.IsNullOrEmpty(ch.title)
                        ? ch.title
                        : ChatHistoryManager.TimestampFromFileName(filePath);
                bool ok = UnityEditor.EditorUtility.DisplayDialog(
                    M("履歴を削除"),
                    string.Format(M("「{0}」を削除しますか？\nこの操作は元に戻せません。"), dlgTitle),
                    M("削除"), M("キャンセル"));
                if (ok) OnSessionDeleted?.Invoke(filePath);
            });
            row.Add(deleteBtn);

            row.RegisterCallback<MouseEnterEvent>(_ => deleteBtn.style.opacity = 0.95f);
            row.RegisterCallback<MouseLeaveEvent>(_ => deleteBtn.style.opacity = 0.35f);

            return row;
        }

        /// <summary>解析済みヘッダーで行を差し替える。</summary>
        void ReplaceRow(string filePath, ChatSessionHeader header)
        {
            if (!_rowsByPath.TryGetValue(filePath, out var oldRow)) return;
            int index = _scrollView.IndexOf(oldRow);
            if (index < 0) return;
            var newRow = BuildRow(filePath, header);
            _scrollView.Insert(index, newRow);
            oldRow.RemoveFromHierarchy();
            _rowsByPath[filePath] = newRow;
            ApplyFilterToRow(newRow, _currentFilter);
        }

        void FilterItems(string query)
        {
            _currentFilter = query ?? "";
            foreach (var child in _scrollView.Children())
                ApplyFilterToRow(child, _currentFilter);
        }

        void ApplyFilterToRow(VisualElement row, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                row.style.display = DisplayStyle.Flex;
                return;
            }
            var headlineLabel = row.Q<Label>(className: "md3-list-item__headline");
            string text = headlineLabel?.text ?? "";
            bool match = text.ToLower().Contains(query.ToLower());
            row.style.display = match ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public void Show() => style.display = DisplayStyle.Flex;
        public void Hide()
        {
            _loadPump?.Pause();
            _loadPump = null;
            style.display = DisplayStyle.None;
        }
        public bool IsVisible => resolvedStyle.display == DisplayStyle.Flex;
    }
}
