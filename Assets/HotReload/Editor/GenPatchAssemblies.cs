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

using static ScriptHotReload.HotReloadUtils;
using static ScriptHotReload.HotReloadConfig;

namespace ScriptHotReload
{
    public class GenPatchAssemblies
    {
        public static bool codeHasChanged => methodsToHook.Count > 0;

        public class MethodData
        {
            public MethodDefinition definition;
            public MethodInfo methodInfo;

            public MethodData(MethodDefinition definition, MethodInfo methodInfo)
            {
                this.definition = definition; this.methodInfo = methodInfo;
            }
        }

        public static Dictionary<string, List<MethodData>> methodsToHook { get; private set; } = new Dictionary<string, List<MethodData>>();

        public static int patchNo { get; private set; } = 0;

        [MenuItem("Tools/DoGenPatchAssemblies")]
        public static void DoGenPatchAssemblies()
        {
            if (!Application.isPlaying)
                return;

            CompileScript.OnCompileSuccess = OnScriptCompileSuccess;
            CompileScript.CompileScriptToDir(kTempCompileToDir);
        }

        public static void OnScriptCompileSuccess(CompileStatus status)
        {
            if (status != CompileStatus.Idle)
                return;

            GenAssemblyAndInfos();
            HookAssemblies.DoHook(methodsToHook);
            if (methodsToHook.Count > 0)
                patchNo++;
        }

        static void GenAssemblyAndInfos()
        {
            methodsToHook.Clear();
            float t = Time.realtimeSinceStartup;
            foreach (var assName in hotReloadAssemblies)
                GenAssemblyDiffInfo(assName);
            t = Time.realtimeSinceStartup - t;
            Debug.Log($"check diff elipse:{t}");

            ShowDiffMethods();
            ModifyAndGenAssemblies();
        }

        static void ShowDiffMethods()
        {
            int count = 0;
            StringBuilder sb = new StringBuilder();
            foreach (var kv in methodsToHook)
            {
                foreach (var md in kv.Value)
                {
                    count++;
                    sb.AppendLine($"{kv.Key}.{md.definition.FullName}");
                }
            }

            if (count > 0)
                Debug.Log($"diff methods count:{count}\n{sb.ToString()}");
            else
                Debug.Log("no diff method");
        }

        /// <summary>
        /// 找出新旧Assembly中的的有差异的函数，目前要求类型和函数的数量和签名均需一致
        /// </summary>
        /// <param name="assName"></param>
        /// <remarks>目前无性能优化，很多都是通过ToString进行差异比较的</remarks>
        static void GenAssemblyDiffInfo(string assName)
        {
            string baseDll = $"{kBuiltinAssembliesDir}/{assName}";
            string lastDll = string.Format(kLastDllPathFormat, Path.GetFileNameWithoutExtension(assName));
            string newDll = $"{kTempCompileToDir}/{assName}";

            if (IsFilesEqual(newDll, lastDll))
                return;

            List<MethodData> methodModified = new List<MethodData>();

            // check diff of newDll and baseDll
            using var baseAssDef = AssemblyDefinition.ReadAssembly(baseDll); // TODO baseDll 可以只读取一次
            using var newAssDef = AssemblyDefinition.ReadAssembly(newDll);

            var baseTypes = baseAssDef.MainModule.Types.ToList();
            var newTypes = newAssDef.MainModule.Types.ToList();
            if(baseTypes.Count != newTypes.Count)
            {
                Debug.LogError($"Assembly [{assName}]'s type count changed dururing play mode, skiped");
                return;
            }

            baseTypes.Sort(TypeDefComparer<TypeDefinition>.comparer);
            newTypes.Sort(TypeDefComparer<TypeDefinition>.comparer);

            for (int i = 0, imax = baseTypes.Count; i < imax;i++)
            {
                var baseT = baseTypes[i];
                var newT = newTypes[i];
                bool typeRet = GenTypeDiffMethodInfos(baseT, newT, methodModified);
                if (!typeRet)
                    return;
            }

            // fill MethodInfo field of MethodData
            {
                Assembly ass = (from ass_ in AppDomain.CurrentDomain.GetAssemblies() where ass_.FullName == baseAssDef.FullName select ass_).FirstOrDefault();
                Debug.Assert(ass != null);

                Dictionary<TypeDefinition, Type> dicType = new Dictionary<TypeDefinition, Type>();
                foreach(var md in methodModified)
                {
                    Type t;
                    MethodDefinition definition = md.definition;
                    if (!dicType.TryGetValue(definition.DeclaringType, out t))
                    {
                        t = ass.GetType(definition.DeclaringType.FullName);
                        Debug.Assert(t != null);
                        dicType.Add(definition.DeclaringType, t);
                    }
                    md.methodInfo = GetMethodInfoSlow(t, definition);
                    if(md.methodInfo == null)
                    {
                        Debug.LogError($"can not find MethodInfo of [{md.definition.FullName}]");
                    }
                }
            }

            if(methodModified.Count > 0)
                methodsToHook.Add(assName, methodModified);
        }

        static bool GenTypeDiffMethodInfos(TypeDefinition baseTypeDef, TypeDefinition newTypeDef, List<MethodData> methodModified)
        {
            if (baseTypeDef.FullName != newTypeDef.FullName)
            {
                Debug.LogError($"Type name mismatched in [{baseTypeDef.Module.Name}.{baseTypeDef.FullName}] , assembly skiped");
                return false;
            }

            var baseMethods = baseTypeDef.Methods.ToList();
            var newMethods = newTypeDef.Methods.ToList();
            baseMethods.Sort(TypeDefComparer<MethodDefinition>.comparer);
            newMethods.Sort(TypeDefComparer<MethodDefinition>.comparer);

            if (baseMethods.Count != newMethods.Count)
            {
                Debug.LogError($"Methods count mismatched in [{baseTypeDef.Module.Name}.{baseTypeDef.FullName}] , assembly skiped");
                return false;
            }

            for (int i = 0, imax = baseMethods.Count; i < imax; i++)
            {
                var baseM = baseMethods[i];
                var newM = newMethods[i];

                if (baseM.IsAbstract || newM.IsAbstract || !baseM.HasBody || !newM.HasBody)
                {
                    continue; // 无函数体的函数跳过
                }

                if (baseM.Name.Contains(".cctor")) // 静态构造函数只会被初始化一次，hook没有意义
                    continue;

                string baseSig = baseM.ToString();
                string newSig = newM.ToString();
                if(baseSig != newSig)
                {
                    Debug.LogError($"Method signature mismatched between {baseTypeDef.Module.Name}.[{baseSig}] and [{newSig}], assembly skiped");
                    return false;
                }

                var baseIns = baseM.Body.Instructions;
                var newIns = newM.Body.Instructions;
                if (baseIns.Count != newIns.Count)
                    methodModified.Add(new MethodData(baseM, null));
                else
                {
                    var arrBaseIns = baseIns.ToArray();
                    var arrNewIns = newIns.ToArray();
                    for (int l = 0, lmax = arrBaseIns.Length; l < lmax; l++)
                    {
                        if (arrBaseIns[l].ToString() != arrNewIns[l].ToString())
                        {
                            methodModified.Add(new MethodData(baseM, null));
                            break;
                        }
                    }
                }
            }

            if (baseTypeDef.NestedTypes.Count != newTypeDef.NestedTypes.Count)
            {
                Debug.Log($"Nested Type count changed in [{baseTypeDef.Module.Name}.{baseTypeDef.FullName}]");
                return false;
            }

            if(baseTypeDef.NestedTypes.Count > 0)
            {
                var baseNestedTypes = baseTypeDef.NestedTypes.ToList();
                var newNestedTypes = newTypeDef.NestedTypes.ToList();

                baseNestedTypes.Sort(TypeDefComparer<TypeDefinition>.comparer);
                newNestedTypes.Sort(TypeDefComparer<TypeDefinition>.comparer);

                for (int j = 0, jmax = baseNestedTypes.Count; j < jmax; j++)
                {
                    var baseNT = baseNestedTypes[j];
                    var newNT = newNestedTypes[j];
                    bool nret = GenTypeDiffMethodInfos(baseNT, newNT, methodModified);
                    if (!nret)
                        return false;
                }
            }

            return true;
        }

        static void ModifyAndGenAssemblies()
        {
            foreach(var assName in hotReloadAssemblies)
            {
                string assNameNoExt = Path.GetFileNameWithoutExtension(assName);

                string lastDll = string.Format(kLastDllPathFormat, assNameNoExt);
                string tmpDll = $"{kTempCompileToDir}/{assName}";
                string patchDll = $"{kTempScriptDir}/{assNameNoExt}_{patchNo}.dll";

                File.Copy(tmpDll, lastDll, true);

                if (methodsToHook.ContainsKey(assName))
                {
                    using var baseAssDef = AssemblyDefinition.ReadAssembly(tmpDll);
                    baseAssDef.Name.Name = $"{baseAssDef.Name}_{patchNo}";
                    baseAssDef.Write(patchDll);
                }
            }

            RemoveAllFiles(kTempCompileToDir);
            Directory.Delete(kTempCompileToDir);
        }

        class TypeDefComparer<T> : IComparer<T> where T: MemberReference
        {
            public static TypeDefComparer<T> comparer = new TypeDefComparer<T>();

            public int Compare(T x, T y)
            {
                return x.FullName.CompareTo(y.FullName);
            }
        }
    }

}
