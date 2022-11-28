using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System.Security.Cryptography;
using System;
using System.Reflection;
using Mono.Cecil;
using UnityEditor;

namespace ScriptHotReload
{
    [InitializeOnLoad]
    public static class PlayModeChangeCallback
    {
        public const string kEditorScriptBuildParamsKey = "kEditorScriptBuildParamsKey";

        static PlayModeChangeCallback()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChange;
        }

        static void OnPlayModeChange(PlayModeStateChange mode)
        {
            switch (mode)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    {
                        HotReloadUtils.RemoveAllFiles(GenPatchAssemblies.kTempScriptDir);
                        if (Directory.Exists(GenPatchAssemblies.kTempCompileToDir))
                            Directory.Delete(GenPatchAssemblies.kTempCompileToDir);

                        CompileScript.ResetCompileStatus();
                        string json = EditorPrefs.GetString(kEditorScriptBuildParamsKey);
                        if (!string.IsNullOrEmpty(json))
                            CompileScript.editorBuildParams = JsonUtility.FromJson<CompileScript.EditorBuildParams>(json);
                        break;
                    }
                case PlayModeStateChange.ExitingEditMode: // 退出编辑模式保存编译参数
                    {
                        string json = JsonUtility.ToJson(CompileScript.editorBuildParams);
                        EditorPrefs.SetString(kEditorScriptBuildParamsKey, json);
                        break;
                    }
                case PlayModeStateChange.ExitingPlayMode:
                    {
                        CompileScript.ResetCompileStatus();
                        if (GenPatchAssemblies.codeHasChanged)
                        {
                            AssetDatabase.Refresh();
                            EditorCompilationWrapper.RequestScriptCompilation("运行过程中代码被修改");
                        }
                        break;
                    }
                case PlayModeStateChange.EnteredEditMode:
                    {
                        HotReloadUtils.RemoveAllFiles(GenPatchAssemblies.kTempScriptDir);
                        break;
                    }
            }
        }
    }
}

