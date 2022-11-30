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
using System.Linq;
using System.Text;

using static ScriptHotReload.HotReloadConfig;
using static ScriptHotReload.HotReloadUtils;

namespace ScriptHotReload
{
    public static class HookAssemblies
    {
        const string kHotReloadHookTag = "kScriptHotReload";

        public static void DoHook(Dictionary<string, List<MethodData>> methodsToHook)
        {
            HookPool.UninstallByData(kHotReloadHookTag);

            foreach(var kv in methodsToHook)
            {
                string assName = kv.Key;
                string patchAssPath = string.Format(kPatchDllPathFormat, Path.GetFileNameWithoutExtension(assName), GenPatchAssemblies.patchNo);
                Assembly patchAssembly = Assembly.LoadFrom(patchAssPath);
                if(patchAssembly == null)
                {
                    Debug.LogError($"Dll Load Fail:{patchAssPath}");
                    continue;
                }

                foreach(var data in kv.Value)
                {
                    MethodInfo miTarget = data.methodInfo;
                    if (miTarget.IsGenericMethod) // 泛型暂时不处理
                        continue;

                    MethodInfo miReplace = GetMethodFromAssembly(miTarget, patchAssembly);
                    if(miReplace == null)
                    {
                        Debug.LogError($"can not find method `{miTarget}` in [{assName}]");
                        continue;
                    }
                    new MethodHook(miTarget, miReplace, null, kHotReloadHookTag).Install();
                }
            }
        }
    }

}
