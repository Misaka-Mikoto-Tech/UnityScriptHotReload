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

            List<MethodBase> methodsOfTypePatch = new List<MethodBase>();
            List<MethodBase> methodsOfTypeOri = new List<MethodBase>();
            foreach (var kv in dicTypesPatch)
            {
                Type patchType = kv.Value;

                /*
                 * 这是编译器自动生成的 lambda 表达式静态类
                 * 其函数名和字段名是自动编号的，即使查找到同名同类型的成员也不一定对应的就是同一个对象
                 * 因此不执行hook
                 */
                if (patchType.FullName.Contains("<>c"))
                    continue;

                Type oriType;
                if (!dicTypesOri.TryGetValue(kv.Key, out oriType))
                    continue; // patch中新增的类型

                if(oriType.ContainsGenericParameters)
                {
                    Type[] genericArgs = oriType.GetGenericArguments();
                    for (int i = 0, imax = genericArgs.Length; i < imax; i++) //泛型类型只用 object 类型填充
                        genericArgs[i] = typeof(object);
                    oriType = oriType.MakeGenericType(genericArgs);
                    patchType = patchType.MakeGenericType(genericArgs);
                }

                methodsOfTypeOri.Clear();
                methodsOfTypePatch.Clear();

                methodsOfTypeOri.AddRange(oriType.GetConstructors());
                methodsOfTypeOri.AddRange(oriType.GetMethods());
                methodsOfTypePatch.AddRange(patchType.GetConstructors());
                methodsOfTypePatch.AddRange(patchType.GetMethods());
                
                foreach(var miPatch in methodsOfTypePatch)
                {
                    if (miPatch.Name == ".cctor")
                        continue;

                    string sig = miPatch.ToString(); // "T TestG[T](T)"
                    var miOri = methodsOfTypeOri.Find(m => m.ToString() == sig);
                    if (miOri != null)
                    {
                        if((miOri is MethodInfo) && miOri.ContainsGenericParameters) // 泛型方法, 使用 object 类型填充
                        {
                            Type[] genericArgs = miOri.GetGenericArguments();
                            for (int i = 0, imax = genericArgs.Length; i < imax; i++)
                                genericArgs[i] = typeof(object);
                            MethodInfo gMiOri = (miOri as MethodInfo).MakeGenericMethod(genericArgs);
                            MethodInfo gMiPatch = (miPatch as MethodInfo).MakeGenericMethod(genericArgs);
                            methodsToHook.Add(gMiOri, gMiPatch);
                        }
                        else
                            methodsToHook.Add(miOri, miPatch);
                    }
                    else
                        Debug.Log($"new method `{sig}` of type `{oriType}`");
                }
            }

            var hookTag = string.Format(kHotReloadHookTag_Fmt, original.GetName().Name);
            foreach (var kv in methodsToHook)
            {
                var miOri = kv.Key;
                var miPatch = kv.Value;

                // 某些重载的函数是相同的地址，比如 struct.Equals()
                if(miOri.MethodHandle.GetFunctionPointer() != miPatch.MethodHandle.GetFunctionPointer())
                {
                    Debug.Log($"Hook Method:{kv.Key.DeclaringType}:{kv.Key}");
                    new MethodHook(kv.Key, kv.Value, null, hookTag).Install();
                }
            }
        }

        static void DoHook(Dictionary<string, List<MethodBase>> methodsToHook)
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
