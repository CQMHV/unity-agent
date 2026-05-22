using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace AjisaiFlow.UnityAgent.Editor
{
    [Serializable]
    public class ChatRecord
    {
        public int type;
        public string text;
        public string thinkingText;
    }

    [Serializable]
    public class ChatSession
    {
        public string title;
        public string timestamp;
        public ChatRecord[] records;
    }

    public static class ChatHistoryManager
    {
        private static readonly string HistoryDir =
            Path.Combine(Application.dataPath, "..", "Library", "UnityAgent", "ChatHistory");

        public static void Save(List<ChatEntry> chatHistory)
        {
            if (chatHistory == null || chatHistory.Count == 0) return;

            Directory.CreateDirectory(HistoryDir);

            var records = new List<ChatRecord>();
            string title = null;

            foreach (var entry in chatHistory)
            {
                records.Add(new ChatRecord
                {
                    type = (int)entry.type,
                    text = entry.text,
                    thinkingText = entry.thinkingText
                });

                if (title == null && entry.type == ChatEntry.EntryType.User)
                {
                    var raw = entry.text;
                    if (raw != null && raw.StartsWith("You: "))
                        raw = raw.Substring(5);
                    if (raw != null && raw.Length > 40)
                        raw = raw.Substring(0, 40) + "...";
                    title = raw;
                }
            }

            if (title == null) title = "チャット";

            var session = new ChatSession
            {
                title = title,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                records = records.ToArray()
            };

            string json = JsonUtility.ToJson(session, true);
            string fileName = DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
            File.WriteAllText(Path.Combine(HistoryDir, fileName), json);
        }

        /// <summary>セッションファイルのパス一覧を新しい順で返す（ファイル内容は読まない、高速）。</summary>
        public static List<string> ListSessionFiles()
        {
            var result = new List<string>();
            if (!Directory.Exists(HistoryDir)) return result;
            var files = Directory.GetFiles(HistoryDir, "*.json");
            Array.Sort(files);
            Array.Reverse(files);
            result.AddRange(files);
            return result;
        }

        /// <summary>1 セッションファイルのヘッダーを解析する。失敗時は null。</summary>
        public static ChatSessionHeader ReadSessionHeader(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath);
                var session = JsonUtility.FromJson<ChatSession>(json);
                if (session == null) return null;
                int msgCount = 0;
                if (session.records != null)
                {
                    foreach (var r in session.records)
                    {
                        if (r.type == (int)ChatEntry.EntryType.User ||
                            r.type == (int)ChatEntry.EntryType.Agent)
                            msgCount++;
                    }
                }
                return new ChatSessionHeader
                {
                    title = session.title,
                    timestamp = session.timestamp,
                    filePath = filePath,
                    messageCount = msgCount
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>ファイル名 "yyyyMMdd_HHmmss.json" から日時文字列を導出する。失敗時はファイル名。</summary>
        public static string TimestampFromFileName(string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            if (DateTime.TryParseExact(name, "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return dt.ToString("yyyy-MM-dd HH:mm:ss");
            return name;
        }

        public static List<ChatEntry> Load(string filePath)
        {
            if (!File.Exists(filePath)) return null;

            string json = File.ReadAllText(filePath);
            var session = JsonUtility.FromJson<ChatSession>(json);
            var entries = new List<ChatEntry>();

            foreach (var record in session.records)
            {
                var entry = new ChatEntry
                {
                    type = (ChatEntry.EntryType)record.type,
                    text = record.text,
                    thinkingText = record.thinkingText
                };
                entries.Add(entry);
            }

            return entries;
        }

        public static void Delete(string filePath)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    public class ChatSessionHeader
    {
        public string title;
        public string timestamp;
        public string filePath;
        public int messageCount;
    }
}
