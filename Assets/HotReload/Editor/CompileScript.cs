using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using static UnityEngine.GraphicsBuffer;
using UnityEditor.Build.Player;
using System.IO;
using UnityEditor.Callbacks;
using System.Reflection;
using MonoHook;
using System.Runtime.CompilerServices;
using System;
using System.Reflection.Emit;

namespace ScriptHotReload
{
    /// <summary>
    /// 手动编译Editor下的脚本到指定目录
    /// </summary>
    public static class CompileScript
    {
        public static CompileStatus compileStatus { get; private set; }

        const string kEditorScriptBuildParamsKey = "kEditorScriptBuildParamsKey";

        public static Action<CompileStatus> OnCompileSuccess;

        [Serializable]
        struct EditorBuildParams
        {
            public EditorScriptCompilationOptions   options;
            public BuildTargetGroup                 platformGroup;
            public BuildTarget                      platform;
            public int                              subtarget;
            public string[]                         extraScriptingDefines;
        }

        static EditorBuildParams s_editorBuildParams;
        static bool s_CompileRequested = false;
        static bool s_codeChanged = false;

        public static void CompileScriptToDir(string outputDir)
        {
            if(!IsIdle())
            {
                Debug.LogError($"当前编译状态:{compileStatus}, 不允许执行编译");
                return;
            }

            // 生成编译配置并指定输出目录
            object scriptAssemblySettings = EditorCompilationWrapper.CreateScriptAssemblySettings(
                s_editorBuildParams.platformGroup, s_editorBuildParams.platform, s_editorBuildParams.options, s_editorBuildParams.extraScriptingDefines, outputDir);
            
            Directory.CreateDirectory(outputDir);
            RemoveAllFiles(outputDir);
            var status = EditorCompilationWrapper.CompileScriptsWithSettings(scriptAssemblySettings);
            Debug.Log($"开始编译dll到目录: {outputDir}");
            s_CompileRequested = true;
            s_codeChanged = true; // TODO 

            ManualTickCompilationPipeline();
        }


        [DidReloadScripts]
        static void Init()
        {
            {// install hook
                var miOri = EditorCompilationWrapper.miTickCompilationPipeline;
                var miNew = typeof(CompileScript).GetMethod(nameof(TickCompilationPipeline), BindingFlags.NonPublic | BindingFlags.Static);
                var miReplace = typeof(CompileScript).GetMethod(nameof(TickCompilationPipeline_Proxy), BindingFlags.NonPublic | BindingFlags.Static);
                new MethodHook(miOri, miNew, miReplace).Install();
            }

            EditorApplication.playModeStateChanged += OnPlayModeChange;
            EditorApplication.update += EditorApplication_Update;
        }

        static void OnPlayModeChange(PlayModeStateChange mode)
        {
            switch(mode)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    {
                        ResetCompileStatus();
                        string json = EditorPrefs.GetString(kEditorScriptBuildParamsKey);
                        if (!string.IsNullOrEmpty(json))
                            s_editorBuildParams = JsonUtility.FromJson<EditorBuildParams>(json);
                        break;
                    }
                case PlayModeStateChange.ExitingEditMode: // 退出编辑模式保存编译参数
                    {
                        string json = JsonUtility.ToJson(s_editorBuildParams);
                        EditorPrefs.SetString(kEditorScriptBuildParamsKey, json);
                        break;
                    }
                case PlayModeStateChange.ExitingPlayMode:
                    {
                        ResetCompileStatus();
                        if (s_codeChanged)
                            EditorCompilationWrapper.RequestScriptCompilation("运行过程中代码被修改");
                        break;
                    }
            }
        }

        static void RemoveAllFiles(string dir)
        {
            string[] files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
                File.Delete(file);
        }

        static void EditorApplication_Update()
        {
            if(s_CompileRequested)
            {
                if(IsIdle())
                {
                    s_CompileRequested = false;
                    OnCompileSuccess?.Invoke(compileStatus);

                    if (compileStatus == CompileStatus.Idle)
                    {
                        Debug.Log("编译已完成");
                    }
                    else
                    {
                        Debug.LogError($"编译失败:{compileStatus}");
                    }
                }
                else if(Application.isPlaying) // PlayMode 下Unity会停止调用 TickCompilationPipeline, 导致编译请求进度无法前进，所以需要我们手动去执行
                {
                    ManualTickCompilationPipeline();
                }
            }
        }

        static bool IsIdle()
        {
            return (compileStatus == CompileStatus.Idle || compileStatus == CompileStatus.CompilationFailed);
        }

        static void ResetCompileStatus()
        {
            s_CompileRequested = false;
            compileStatus = CompileStatus.Idle;
        }

        static void ManualTickCompilationPipeline()
        {
            compileStatus = EditorCompilationWrapper.TickCompilationPipeline(
                        s_editorBuildParams.options, s_editorBuildParams.platformGroup, s_editorBuildParams.platform,
                        s_editorBuildParams.subtarget, s_editorBuildParams.extraScriptingDefines);
        }

        /// <summary>
        /// 拦截Unity自己的Editor编译函数获取编译参数
        /// </summary>
        /// <param name="options">type:EditorScriptCompilationOptions</param>
        /// <remarks>此函数每帧都会被调用，即使当前无需编译</remarks>
        static CompileStatus TickCompilationPipeline(EditorScriptCompilationOptions options, BuildTargetGroup platfromGroup, BuildTarget platform, int subtarget, string[] extraScriptingDefines)
        {
            s_editorBuildParams.options = options;
            s_editorBuildParams.platformGroup = platfromGroup;
            s_editorBuildParams.platform = platform;
            s_editorBuildParams.subtarget = subtarget;
            s_editorBuildParams.extraScriptingDefines = extraScriptingDefines;

            compileStatus = TickCompilationPipeline_Proxy(options, platfromGroup, platform, subtarget, extraScriptingDefines);
            //Debug.Log($"TickCompilationPipleline with status:{s_CompileStatus}");
            return compileStatus;
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        static CompileStatus TickCompilationPipeline_Proxy(EditorScriptCompilationOptions options, BuildTargetGroup platfromGroup, BuildTarget platform, int subtarget, string[] extraScriptingDefines)
        {
            Debug.Log($"dummy code " + platfromGroup.GetType());
            return CompileStatus.Idle;
        }
    }
}
