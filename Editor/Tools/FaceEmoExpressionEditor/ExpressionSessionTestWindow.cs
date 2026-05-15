// Editor/Tools/FaceEmoExpressionEditor/ExpressionSessionTestWindow.cs
using System.Linq;
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
            if (GUILayout.Button("Spike 0.2: Resolve IExpressionEditor"))
            {
                SpikeResolveIExpressionEditor();
            }
            if (GUILayout.Button("Spike 0.3: Get ExpressionEditorModelFacade"))
            {
                SpikeGetFacade();
            }

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

        private void SpikeResolveIExpressionEditor()
        {
            Log("--- Spike 0.2 ---");
#if FACE_EMO
            var launcher = FaceEmoAPI.FindLauncher();
            if (launcher == null) { Log("FAIL: No FaceEmoLauncher in scene."); return; }

            try
            {
                var installerType = System.Type.GetType(
                    "Suzuryg.FaceEmo.AppMain.FaceEmoInstaller, jp.suzuryg.face-emo.appmain.Editor");
                if (installerType == null) { Log("FAIL: FaceEmoInstaller type not found."); return; }

                var installer = System.Activator.CreateInstance(installerType, new object[] { launcher.gameObject });
                var container = installerType.GetProperty("Container").GetValue(installer);

                var ieeType = System.Type.GetType(
                    "Suzuryg.FaceEmo.Detail.ExpressionEditor.IExpressionEditor, jp.suzuryg.face-emo.detail.Editor");
                if (ieeType == null) { Log("FAIL: IExpressionEditor type not found."); return; }

                var resolveMethod = container.GetType()
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Resolve"
                        && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (resolveMethod == null) { Log("FAIL: Resolve<T>() not found."); return; }

                var ee = resolveMethod.MakeGenericMethod(ieeType).Invoke(container, null);
                Log($"OK: IExpressionEditor resolved → {ee?.GetType().FullName ?? "null"}");
            }
            catch (System.Exception ex)
            {
                Log($"FAIL: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
            }
#else
            Log("SKIP: FACE_EMO not defined.");
#endif
        }

        private void SpikeGetFacade()
        {
            Log("--- Spike 0.3 ---");
#if FACE_EMO
            var launcher = FaceEmoAPI.FindLauncher();
            if (launcher == null) { Log("FAIL: No launcher."); return; }
            try
            {
                // Resolve IExpressionEditor as in 0.2
                var installerType = System.Type.GetType("Suzuryg.FaceEmo.AppMain.FaceEmoInstaller, jp.suzuryg.face-emo.appmain.Editor");
                var installer = System.Activator.CreateInstance(installerType, new object[] { launcher.gameObject });
                var container = installerType.GetProperty("Container").GetValue(installer);
                var ieeType = System.Type.GetType("Suzuryg.FaceEmo.Detail.ExpressionEditor.IExpressionEditor, jp.suzuryg.face-emo.detail.Editor");
                var resolve = container.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "Resolve" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                var ee = resolve.MakeGenericMethod(ieeType).Invoke(container, null);

                // Probe all instance fields/props for ExpressionEditorModelFacade
                var facadeTypeName = "ExpressionEditorModelFacade";
                var eeType = ee.GetType();
                Log($"Probing {eeType.FullName} for facade...");

                var allFields = eeType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                foreach (var f in allFields)
                {
                    if (f.FieldType.Name == facadeTypeName)
                    {
                        var v = f.GetValue(ee);
                        Log($"FOUND field '{f.Name}' → {v?.GetType().FullName ?? "null"}");
                        if (v != null)
                        {
                            // dump available methods
                            foreach (var m in v.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            {
                                if (m.DeclaringType == v.GetType())
                                    Log($"  method: {m.Name}({m.GetParameters().Length} args)");
                            }
                        }
                        return;
                    }
                }
                Log("FAIL: No field of type ExpressionEditorModelFacade on IExpressionEditor impl.");

                // Optional: also probe presenter
                // (record in notes if direct field access fails — may need presenter-mediated path)
            }
            catch (System.Exception ex)
            {
                Log($"FAIL: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
            }
#else
            Log("SKIP: FACE_EMO not defined.");
#endif
        }
    }
}
