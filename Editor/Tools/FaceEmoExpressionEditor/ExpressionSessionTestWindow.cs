// Editor/Tools/FaceEmoExpressionEditor/ExpressionSessionTestWindow.cs
using UnityEditor;
using UnityEngine;

namespace AjisaiFlow.UnityAgent.Editor.Tools.FaceEmoExpressionEditor
{
    public class ExpressionSessionTestWindow : EditorWindow
    {
        private Vector2 _scroll;
        private string _log = "";

        [MenuItem("Window/AjisaiFlow/Expression Session Test")]
        public static void Open() => GetWindow<ExpressionSessionTestWindow>("ExprSession Test");

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Phase 0: Spikes", EditorStyles.boldLabel);
            // Buttons added per spike task

            EditorGUILayout.LabelField("Log:", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            EditorGUILayout.TextArea(_log, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button("Clear")) _log = "";
        }

        private void Log(string msg)
        {
            _log += msg + "\n";
            Debug.Log("[ExprSessionTest] " + msg);
            Repaint();
        }
    }
}
