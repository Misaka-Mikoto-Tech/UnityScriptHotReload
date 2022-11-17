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

namespace ScriptHotReload
{
    /// <summary>
    /// 手动编译Editor下的脚本到指定目录
    /// </summary>
    public static class CompileScript
    {
        public static CompileStatus compileStatus { get; private set; }

        const string kTempScriptDir = "Temp/ScriptHotReload";
        const string kEditorScriptBuildParamsKey = "kEditorScriptBuildParamsKey";

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

        static object s_objEditorCompilation;
        static MethodInfo s_miCreateScriptAssemblySettings;
        static MethodInfo s_CompileScriptsWithSettings;
        static MethodInfo s_setOutputDirectory;

        [MenuItem("Tools/ScriptHotReload")]
        public static void BuildToTempDir()
        {
            CompileScriptToDir(kTempScriptDir);
        }

        public static void CompileScriptToDir(string buildDir)
        {
            if(compileStatus != CompileStatus.Idle)
            {
                Debug.LogError($"当前编译状态:{compileStatus}, 不允许执行编译");
                return;
            }

            object scriptAssemblySettings = s_miCreateScriptAssemblySettings.Invoke(s_objEditorCompilation,
                new object[] { s_editorBuildParams.platformGroup, s_editorBuildParams.platform, (int)s_editorBuildParams.options, s_editorBuildParams.extraScriptingDefines });
            // 修改输出目录
            Directory.CreateDirectory(buildDir);
            s_setOutputDirectory.Invoke(scriptAssemblySettings, new object[] { buildDir });
            var status = s_CompileScriptsWithSettings.Invoke(s_objEditorCompilation, new object[] { scriptAssemblySettings });
            Debug.Log($"CompileScriptToDir, status:{status}");
        }


        [DidReloadScripts]
        static void InitHooksAndGetMethods()
        {
            var tInterface = typeof(UnityEditor.Scripting.ManagedDebugger).Assembly.GetType("UnityEditor.Scripting.ScriptCompilation.EditorCompilationInterface");
            var tCompilation = typeof(UnityEditor.Scripting.ManagedDebugger).Assembly.GetType("UnityEditor.Scripting.ScriptCompilation.EditorCompilation");
            var tAssemblySettings = typeof(UnityEditor.Scripting.ManagedDebugger).Assembly.GetType("UnityEditor.Scripting.ScriptCompilation.ScriptAssemblySettings");

            {
                var miOri = tInterface.GetMethod("TickCompilationPipeline", BindingFlags.Static | BindingFlags.Public);
                var miNew = typeof(CompileScript).GetMethod(nameof(TickCompilationPipeline), BindingFlags.NonPublic | BindingFlags.Static);
                var miReplace = typeof(CompileScript).GetMethod(nameof(TickCompilationPipeline_Proxy), BindingFlags.NonPublic | BindingFlags.Static);
                new MethodHook(miOri, miNew, miReplace).Install();
            }

            {
                s_objEditorCompilation = tInterface.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetGetMethod().Invoke(null, null);
                Debug.Log($"s_objEditorCompilation != null:{s_objEditorCompilation != null}");
                var mis = tCompilation.GetMethods();
                foreach(var mi in mis)
                {
                    if (mi.Name == "CreateScriptAssemblySettings" && mi.GetParameters().Length == 4)
                        s_miCreateScriptAssemblySettings = mi;
                    else if (mi.Name == "CompileScriptsWithSettings")
                        s_CompileScriptsWithSettings = mi;
                }

                Debug.Assert(s_miCreateScriptAssemblySettings != null);
                Debug.Assert(s_CompileScriptsWithSettings != null);

                s_setOutputDirectory = tAssemblySettings.GetProperty("OutputDirectory", BindingFlags.Public).GetSetMethod();
                Debug.Assert(s_setOutputDirectory != null);
            }

            EditorApplication.playModeStateChanged += OnPlayModeChange;
        }

        static void OnPlayModeChange(PlayModeStateChange mode)
        {
            switch(mode)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    {
                        string json = EditorPrefs.GetString(kEditorScriptBuildParamsKey);
                        if (!string.IsNullOrEmpty(json))
                            s_editorBuildParams = JsonUtility.FromJson<EditorBuildParams>(json);
                        break;
                    }
                case PlayModeStateChange.ExitingEditMode:
                    {
                        string json = JsonUtility.ToJson(s_editorBuildParams);
                        EditorPrefs.SetString(kEditorScriptBuildParamsKey, json);
                        break;
                    }
            }
        }

        public enum CompileStatus
        {
            Idle,
            Compiling,
            CompilationStarted,
            CompilationFailed,
            CompilationComplete
        }

        [Flags]
        enum EditorScriptCompilationOptions
        {
            BuildingEmpty                               = 0,
            BuildingDevelopmentBuild                    = 1 << 0,
            BuildingForEditor                           = 1 << 1,
            BuildingEditorOnlyAssembly                  = 1 << 2,
            BuildingForIl2Cpp                           = 1 << 3,
            BuildingWithAsserts                         = 1 << 4,
            BuildingIncludingTestAssemblies             = 1 << 5,
            BuildingPredefinedAssembliesAllowUnsafeCode = 1 << 6,
            BuildingForHeadlessPlayer                   = 1 << 7,
            BuildingUseDeterministicCompilation         = 1 << 9,
            BuildingWithRoslynAnalysis                  = 1 << 10,
            BuildingWithoutScriptUpdater                = 1 << 11
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
