/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.IO;
using UnityEditor.Callbacks;
using System.Reflection;
using MonoHook;
using System;
using System.Linq;
using System.Text;

namespace ScriptHotReload
{
    public static class HookAssemblies
    {
        const string kWrapperClassFullName = "ScriptHotReload.__Patch_GenericInst_Wrapper__Gen__";
        const string kGetGenericInstMethodForPatch = "GetGenericInstMethodForPatch";
        const string kHotReloadHookTag_Fmt = "kScriptHotReload_{0}";

        public static void DoHook(Assembly original, Assembly patch)
        {
            Dictionary<string, Type> dicTypesOri = new Dictionary<string, Type>();
            Dictionary<string, Type> dicTypesPatch = new Dictionary<string, Type>();

            foreach (var t in original.GetTypes()) // 包含 NestedClass
                if(!t.IsAbstract && (t.IsClass || t.IsValueType || t.IsInterface)) // interface 允许包含默认方法
                    dicTypesOri.Add(t.FullName, t);

            foreach (var t in patch.GetTypes())
                if (!t.IsAbstract && (t.IsClass || t.IsValueType || t.IsInterface)) // interface 允许包含默认方法
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

                if(patchType.FullName == kWrapperClassFullName)
                    continue;

                Type oriType;
                if (!dicTypesOri.TryGetValue(kv.Key, out oriType))
                    continue; // patch中新增的类型

                if (oriType.ContainsGenericParameters) // 第一遍只处理非泛型类型
                    continue;

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

                    if(miPatch.ContainsGenericParameters) // 所属类型无泛型，但函数包含泛型的也算泛型类型
                        continue;

                    if(miPatch.IsAbstract || miPatch.GetMethodBody().GetILAsByteArray().Length == 0)
                        continue;

                    string sig = miPatch.ToString();
                    var miOri = methodsOfTypeOri.Find(m => m.ToString() == sig);
                    if (miOri != null)
                    {
                        // 将纯虚方法修改为非纯虚方法不进行Hook（因为原始方法根本不会被jit，没有函数体可以用来填充jmp代码）
                        if (miOri.IsAbstract || miOri.GetMethodBody().GetILAsByteArray().Length == 0)
                            continue;

                        methodsToHook.Add(miOri, miPatch);
                    }
                    else
                        Debug.Log($"new method `{sig}` of type `{oriType}`");
                }
            }

            // 从patch dll内获取提前生成好的，并构建实例化后的hook方法映射字典
            var genericInstHookPairs = GetGenericMethodInstDic(patch);
            foreach (var genericInst in genericInstHookPairs)
                methodsToHook.Add(genericInst.Key, genericInst.Value);

            var hookTag = string.Format(kHotReloadHookTag_Fmt, original.GetName().Name);
            HookPool.UninstallByTag(hookTag);
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

        public static void UnHookDlls(List<string> dllNames)
        {
            foreach (var dll in dllNames)
            {
                var hookTag = string.Format(kHotReloadHookTag_Fmt, dll);
                HookPool.UninstallByTag(hookTag);
            }
        }

        /// <summary>
        /// 获取 patch dll 内预先生成的需要hook的泛型实例字典
        /// </summary>
        /// <returns></returns>
        static Dictionary<MethodBase, MethodBase> GetGenericMethodInstDic(Assembly patch)
        {
            var wrapperType = patch.GetType(kWrapperClassFullName);
            var genMi = wrapperType.GetMethod(kGetGenericInstMethodForPatch);
            object ret;
            try
            {
                ret = genMi.Invoke(null, new object[0]);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                throw;
            }
            return ret as Dictionary<MethodBase, MethodBase>;
        }

        static BindingFlags GetBindingFlags(MethodBase srcMethod)
        {
            BindingFlags flag = BindingFlags.Default;
            flag |= srcMethod.IsPublic ? BindingFlags.Public : BindingFlags.NonPublic;
            flag |= srcMethod.IsStatic ? BindingFlags.Static : BindingFlags.Instance;
            return flag;
        }
    }

}
