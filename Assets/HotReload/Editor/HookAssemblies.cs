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

            Type wrapperType = null;
            // 类型或者类型内定义的方法包含泛型参数的类型集合
            Dictionary<Type, Type> genericTypes = new Dictionary<Type, Type>();
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

                if(patchType.FullName == "ScriptHotReload.<>__GenericInstWrapper__")
                {
                    wrapperType = patchType;
                    continue;
                }

                Type oriType;
                if (!dicTypesOri.TryGetValue(kv.Key, out oriType))
                    continue; // patch中新增的类型

                if (oriType.ContainsGenericParameters) // 第一遍只处理非泛型类型
                {
                    genericTypes.Add(oriType, patchType);
                    continue;
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

                    if(miPatch.ContainsGenericParameters) // 所属类型无泛型，但函数包含泛型的也添加进集合
                    {
                        genericTypes.TryAdd(oriType, patchType);
                        continue;
                    }

                    string sig = miPatch.ToString(); // "T TestG[T](T)"
                    var miOri = methodsOfTypeOri.Find(m => m.ToString() == sig);
                    if (miOri != null)
                    {
                        methodsToHook.Add(miOri, miPatch);
                    }
                    else
                        Debug.Log($"new method `{sig}` of type `{oriType}`");
                }
            }

            // 处理泛型方法
            if (wrapperType == null)
                throw new Exception("can not find wrapper type");
            // 收集并构建实例化后的hook方法映射数据
            var genericInstHookPairs = GenGenericInstHookPairs(wrapperType, genericTypes);
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


        class WrapperItem
        {
            public MethodBase wrapperMethod;
            public Type[] typeGenArgs;
        }

        class WrapperData
        {
            public int idx;
            public MethodBase genericMethodOri; // 原始dll内的泛型方法
            public List<WrapperItem> wrappers = new List<WrapperItem>();
        }

        /// <summary>
        /// 获取patch dll里创建的wrapper函数数据
        /// </summary>
        /// <param name="wrapperType"></param>
        /// <param name="genericTypes">key:oriType, value:patchType</param>
        /// <returns></returns>
        static List<WrapperData> CollectMethodWrappers(Type wrapperType, Dictionary<Type, Type> genericTypes)
        {
            var dicWrapperData = new Dictionary<int, WrapperData>();

            {// 收集 patch dll 内的 wrapper 定义
                var mis = wrapperType.GetMethods(BindingFlags.Static | BindingFlags.Public);
                foreach (var m in mis)
                {
                    var wrapperAttr = m.GetCustomAttribute<GenericMethodWrapperAttribute>();
                    if (wrapperAttr == null)
                        continue;

                    if (!dicWrapperData.TryGetValue(wrapperAttr.index, out var wrapper))
                    {
                        wrapper = new WrapperData() { idx = wrapper.idx };
                        dicWrapperData.Add(wrapper.idx, wrapper);
                    }
                    wrapper.wrappers.Add(new WrapperItem() { wrapperMethod = m, typeGenArgs = wrapperAttr.typeGenArgs });
                }
            }

            // 扫描wrapper对应的泛型定义
            foreach(var (typeOri, typePatch) in genericTypes)
            {
                if (typePatch.Name.Contains("<>c") || typePatch == wrapperType) // lambda 表达式的自动生成类
                    continue;

                List<MethodBase> misOri = new List<MethodBase>();
                List<MethodBase> misPatch = new List<MethodBase>();
                misOri.AddRange(typeOri.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic));
                misOri.AddRange(typeOri.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
                misPatch.AddRange(typePatch.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic));
                misPatch.AddRange(typePatch.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

                foreach (var mPatch in misPatch)
                {
                    var idxAttr = mPatch.GetCustomAttribute<GenericMethodIndexAttribute>();
                    if (idxAttr == null)
                        continue;

                    if (!dicWrapperData.TryGetValue(idxAttr.index, out var wrapper))
                        throw new Exception($"can not find wrapper data with idx:{idxAttr.index}");

                    string mSig = mPatch.ToString();
                    foreach (var mOri in misOri) // 找出原始 dll 内的同名泛型方法
                    {
                        if(mOri.ToString() == mSig)
                        {
                            wrapper.genericMethodOri = mOri;
                            break;
                        }
                    }
                }
            }
            return dicWrapperData.Values.ToList();
        }

        /// <summary>
        /// 构建hook相关的方法对
        /// </summary>
        /// <param name="wrapperDatas"></param>
        /// <returns></returns>
        static Dictionary<MethodBase, MethodBase> GenGenericInstHookPairs(Type wrapperType, Dictionary<Type, Type> genericTypes)
        {
            var ret = new Dictionary<MethodBase, MethodBase>();

            List<WrapperData> wrapperDatas = CollectMethodWrappers(wrapperType, genericTypes);

            foreach(var wrapperData in wrapperDatas)
            {
                MethodBase genericMethodOri = wrapperData.genericMethodOri;
                Type type = genericMethodOri.DeclaringType;

                Type[] genTypeArgs = type.GetGenericArguments();
                Type[] genMethodArgs = genericMethodOri.GetGenericArguments();

                int typeGenCnt = type.GenericTypeArguments.Length;
                int methodGenCnt = genericMethodOri.GetGenericArguments().Length;

                foreach (var wrapper in wrapperData.wrappers)
                {
                    Type finalType = type;
                    MethodBase finallyMi = genericMethodOri;
                    if (typeGenCnt > 0)
                    {
                        for (int i = 0, imax = genTypeArgs.Length; i < imax; i++)
                            genTypeArgs[i] = wrapper.typeGenArgs[i];
                        finalType = type.MakeGenericType(genTypeArgs);

                        // 实例化的类型内的方法 MetadataToken 不变，因此可以通过此值匹配
                        if(finallyMi is ConstructorInfo)
                            finallyMi = (from m in finalType.GetConstructors(GetBindingFlags(finallyMi))
                                         where m.MetadataToken == finallyMi.MetadataToken
                                         select m).First();
                        else
                            finallyMi = (from m in finalType.GetMethods(GetBindingFlags(finallyMi))
                                     where m.MetadataToken == finallyMi.MetadataToken select m).First();
                    }

                    if (methodGenCnt > 0 && (genericMethodOri is not ConstructorInfo)) // 没有泛型构造方法这种东西
                    {
                        for (int i = 0, imax = genMethodArgs.Length; i < imax; i++)
                            genMethodArgs[i] = wrapper.typeGenArgs[typeGenCnt + i];
                        finallyMi = (genericMethodOri as MethodInfo).MakeGenericMethod(genMethodArgs);
                    }

                    ret.Add(finallyMi, wrapper.wrapperMethod);
                }
            }
            return ret;
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
