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
using Mono;
using Mono.Cecil;

namespace ScriptHotReload
{
    public class GenPatchAssemblies
    {
        public class AssemblyDiffInfo
        {
            public Dictionary<Type, List<MethodInfo>> modified;
            public Dictionary<Type, List<MethodInfo>> removed;
            public Dictionary<Type, List<MethodInfo>> added;
        }

        /// <summary>
        /// 需要执行 HotReload 的程序集名称
        /// </summary>
        public static List<string> hotReloadAssemblies = new List<string>()
        {
            "Assembly-CSharp.dll"
        };

        public static Dictionary<string, AssemblyDiffInfo> dic_diffInfos = new Dictionary<string, AssemblyDiffInfo>();

        public const string kTempScriptDir = "Temp/ScriptHotReload";
        public const string kTempCompileToDir = "Temp/ScriptHotReload/tmp";
        const string kBuiltinAssembliesDir = "Library/ScriptAssemblies";

        static int currPatchCount = 0;

        [MenuItem("Tools/DoGenPatchAssemblies")]
        public static void DoGenPatchAssemblies()
        {
            CompileScript.OnCompileSuccess = OnScriptCompileSuccess;
            CompileScript.CompileScriptToDir(kTempCompileToDir);
        }

        static void OnScriptCompileSuccess(CompileStatus status)
        {
            if (status != CompileStatus.Idle)
                return;

            CompareAssemblies();
        }

        static void CompareAssemblies()
        {
            dic_diffInfos.Clear();
            foreach (var assName in hotReloadAssemblies)
                GenAssemblyDiffInfo(assName);
        }

        static void GenAssemblyDiffInfo(string assName)
        {

        }
    }

}
