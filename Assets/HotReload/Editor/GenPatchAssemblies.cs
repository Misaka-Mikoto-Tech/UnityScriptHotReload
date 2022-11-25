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
using static ScriptHotReload.HotReloadUtils;
using System.Linq;
using System.Text;

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

        public static Dictionary<string, List<MethodDefinition>> dic_diffInfos { get; private set; } = new Dictionary<string, List<MethodDefinition>>();

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
            float t = Time.realtimeSinceStartup;
            foreach (var assName in hotReloadAssemblies)
                GenAssemblyDiffInfo(assName);
            t = Time.realtimeSinceStartup - t;
            Debug.Log($"check diff elipse:{t}");

            ShowDiffMethods();
        }

        static void ShowDiffMethods()
        {
            int count = 0;
            StringBuilder sb = new StringBuilder();
            foreach (var kv in dic_diffInfos)
            {
                foreach (var md in kv.Value)
                {
                    count++;
                    sb.AppendLine($"{kv.Key}.{md.FullName}");
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
        static void GenAssemblyDiffInfo(string assName)
        {
            string baseDll = $"{kBuiltinAssembliesDir}/{assName}";
            string lastDll = $"{kTempScriptDir}/{Path.GetFileNameWithoutExtension(assName)}__last.dll";
            string newDll = $"{kTempCompileToDir}/{assName}";

            if (IsFilesEqual(baseDll, lastDll))
                return;

            //File.Copy(newDll, lastDll, true);

            List<MethodDefinition> methodModified = new List<MethodDefinition>();

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
                if(baseT.FullName != newT.FullName)
                {
                    Debug.LogError($"Types mismatched in assembly {assName} between {baseT.FullName} and {newT.FullName}");
                    return;
                }

                var baseMethods = baseT.Methods.ToList();
                var newMethods = newT.Methods.ToList();
                baseMethods.Sort(TypeDefComparer<MethodDefinition>.comparer);
                newMethods.Sort(TypeDefComparer<MethodDefinition>.comparer);

                if(baseMethods.Count != newMethods.Count)
                {
                    Debug.LogError($"Methods count mismatched in [{assName}.{baseT.FullName}] , assembly skiped");
                    return;
                }

                for(int j = 0, jmax = baseMethods.Count; j < jmax; j++)
                {
                    var baseM = baseMethods[j];
                    var newM = newMethods[j];
                    if(baseM.FullName != newM.FullName)
                    {
                        Debug.LogError($"Method name mismatched in [{assName}.{baseT.FullName}] between {baseM.Name} and {newM.Name}");
                        return;
                    }

                    if(baseM.IsAbstract || newM.IsAbstract || !baseM.HasBody || !newM.HasBody)
                    {
                        continue; // 无函数体的函数跳过
                    }

                    if(baseM.ReturnType.FullName != newM.ReturnType.FullName || baseM.Parameters.Count != newM.Parameters.Count)
                    {
                        Debug.LogError($"Method return type or parameter count mismatched in [{assName}.{baseT.FullName}.{baseM.Name}]");
                        return;
                    }
                    for(int k = 0, kmax = baseM.Parameters.Count; k < kmax;k++)
                    {
                        var baseParaT = baseM.Parameters[k].ParameterType;
                        var newParaT = newM.Parameters[k].ParameterType;
                        if (baseParaT.FullName != newParaT.FullName)
                        {
                            Debug.LogError($"Parameter[{k}] type mismatched in [{assName}.{baseT.FullName}.{baseM.Name}] between {baseParaT} and {newParaT}");
                            return;
                        }
                    }

                    var baseIns = baseM.Body.Instructions;
                    var newIns = newM.Body.Instructions;
                    if (baseIns.Count != newIns.Count)
                        methodModified.Add(baseM);
                    else
                    {
                        var arrBaseIns = baseIns.ToArray();
                        var arrNewIns = newIns.ToArray();
                        for(int l = 0, lmax = arrBaseIns.Length; l < lmax;l++)
                        {
                            if (arrBaseIns[l].ToString() != arrNewIns[l].ToString())
                            {
                                methodModified.Add(baseM);
                                break;
                            }
                        }
                    }
                }
            }

            dic_diffInfos.Add(assName, methodModified);
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
