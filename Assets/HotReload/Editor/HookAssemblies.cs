/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */
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
using System.Linq;
using System.Text;

using static ScriptHotReload.HotReloadConfig;
using static ScriptHotReload.HotReloadUtils;

namespace ScriptHotReload
{
    public static class HookAssemblies
    {
        const string kHotReloadHookTag_Fmt = "kScriptHotReload_{0}";

        public static void DoHook(Dictionary<string, List<MethodBase>> methodsToHook)
        {
            foreach(var kv in methodsToHook)
            {
                string assName = Path.GetFileNameWithoutExtension(kv.Key);
                var hookTag = string.Format(kHotReloadHookTag_Fmt, assName);
                HookPool.UninstallByTag(hookTag);

                string patchAssPath = string.Format(kPatchDllPathFormat, assName, HotReloadExecutor.patchNo);
                Assembly patchAssembly = Assembly.LoadFrom(patchAssPath);
                if(patchAssembly == null)
                {
                    Debug.LogError($"Dll Load Fail:{patchAssPath}");
                    continue;
                }

                foreach(var method in kv.Value)
                {
                    Debug.LogFormat("Try Hook Method:{0}", method.Name);
                    MethodBase miTarget = method;
                    if (miTarget.ContainsGenericParameters) // 泛型暂时不处理
                        continue;

                    MethodBase miReplace = GetMethodFromAssembly(miTarget, patchAssembly);
                    if(miReplace == null)
                    {
                        Debug.LogError($"can not find method `{miTarget}` in [{assName}.dll]");
                        continue;
                    }
                    try
                    {
                        new MethodHook(miTarget, miReplace, null, hookTag).Install();
                    }
                    catch(Exception ex)
                    {
                        Debug.Log(ex.Message);
                        throw;
                    }
                }
            }
        }

        public static void UnHook(Dictionary<string, List<MethodBase>> methodsToHook)
        {
            foreach (var kv in methodsToHook)
            {
                string assName = kv.Key;
                var hookTag = string.Format(kHotReloadHookTag_Fmt, assName);
                HookPool.UninstallByTag(hookTag);
            }
        }
    }

}
