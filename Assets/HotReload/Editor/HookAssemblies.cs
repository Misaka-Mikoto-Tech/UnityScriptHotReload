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

        public static void DoHook(Assembly original, Assembly patch)
        {
            Dictionary<string, Type> dicTypesOri = new Dictionary<string, Type>();
            Dictionary<string, Type> dicTypesPatch = new Dictionary<string, Type>();

            foreach (var t in original.GetTypes()) // 包含 NestedClass
                dicTypesOri.Add(t.FullName, t);

            foreach (var t in patch.GetTypes())
                dicTypesPatch.Add(t.FullName, t);

            Dictionary<MethodBase, MethodBase> methodsToHook = new Dictionary<MethodBase, MethodBase>(); // original, patch
            foreach(var kv in dicTypesPatch)
            {
                Type oriType;
                if (!dicTypesOri.TryGetValue(kv.Key, out oriType))
                    continue; // patch中新增的类型

                var ctors = kv.Value.GetConstructors();

                var mis = kv.Value.GetMethods();

            }
        }

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

        public static void UnHookDlls(List<string> dllNames)
        {
            foreach (var dll in dllNames)
            {
                var hookTag = string.Format(kHotReloadHookTag_Fmt, dll);
                HookPool.UninstallByTag(hookTag);
            }
        }
    }

}
