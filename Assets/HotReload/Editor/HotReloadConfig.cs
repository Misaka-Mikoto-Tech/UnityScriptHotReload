/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */

using System;
using System.Collections.Generic;
using System.IO;

namespace ScriptHotReload
{
    public class HotReloadConfig
    {
        /// <summary>
        /// 是否启用，运行时修改无效
        /// </summary>
        public readonly static bool hotReloadEnabled = true;
        /// <summary>
        /// 自动重载模式下Patch检测时间间隔
        /// </summary>
        public const float  kAutoReloadPatchCheckSpan    = 2.5f;
        public const string kPatchAssemblyName      = "{0}_Patch_{1}";  // {0}: AssemblyName, {1}: PatchNo

        public const string kTempScriptDir          = "Temp/ScriptHotReload";
        public const string kBuiltinAssembliesDir   = "Library/ScriptAssemblies";
        public const string kPatchDllPathFormat     = kTempScriptDir + "/"+ kPatchAssemblyName +".dll";

        public const string kAssemblyPatcherInput = kTempScriptDir + "/InputArgs.json";
        public const string kAssemblyPatcherOutput = kTempScriptDir + "/OutputReport.json";


        public const string kEditorScriptBuildParamsKey = "kEditorScriptBuildParamsKey";
        /// <summary>
        /// 类型内 lambda 表达式的封装类名称，代码修改后lambda表达式对应的函数名可能会发生改变，因此特殊处理
        /// </summary>
        /// <remarks>注意：只修改lambda表达式，但不修改调用处代码，将不会被检测到改变</remarks>
        public const string kLambdaWrapperBackend = "/<>c";

        static HotReloadConfig()
        {
            if(!Directory.Exists(kTempScriptDir))
            {
                Directory.CreateDirectory(kTempScriptDir);
            }
        }

        /// <summary>
        /// 变动文件是否需要被忽略，不想被reload的文件返回true即可
        /// </summary>
        /// <remarks>/HotReload/Editor/目录下的默认被过滤</remarks>
        public static Func<string, bool> fileShouldIgnore = path =>
        {
            /*
             * eg.
             * if(path.Contains("auto_gen"))
             *     return true;
             *  return false;
             */

            return false;
        };

    }

}
