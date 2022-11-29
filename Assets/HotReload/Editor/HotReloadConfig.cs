using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ScriptHotReload
{
    public class HotReloadConfig
    {
        /// <summary>
        /// 需要执行 HotReload 的程序集名称
        /// </summary>
        public static List<string> hotReloadAssemblies = new List<string>()
        {
            "Assembly-CSharp.dll"
        };

        public const string kTempScriptDir = "Temp/ScriptHotReload";
        public const string kTempCompileToDir = "Temp/ScriptHotReload/tmp";
        public const string kBuiltinAssembliesDir = "Library/ScriptAssemblies";
        public const string kLastDllPathFormat = kTempScriptDir + "/{0}__last.dll"; // {0}:assNameNoExt
        public const string kPatchDllPathFormat = kTempScriptDir + "/{0}_{1}.dll"; // {0}:assNameNoExt, {1}:PatchNo

        public const string kEditorScriptBuildParamsKey = "kEditorScriptBuildParamsKey";

    }

}
