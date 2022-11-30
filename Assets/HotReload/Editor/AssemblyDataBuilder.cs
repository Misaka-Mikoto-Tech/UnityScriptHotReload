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
using NUnit.Framework;
using System.Buffers.Text;

namespace ScriptHotReload
{
    public class AssemblyData
    {
        public string       name;
        public Assembly     assembly;
        public Dictionary<string, TypeData> baseTypes       = new Dictionary<string, TypeData>();
        public Dictionary<string, TypeData> newTypes        = new Dictionary<string, TypeData>();
        public List<MethodData>             methodModified  = new List<MethodData>(); // new method data
    }

    public class TypeData
    {
        public TypeDefinition   definition;
        public Type             type;
        public Dictionary<string, MethodData>       methods = new Dictionary<string, MethodData>();
        public Dictionary<string, FieldDefinition>  staticFields = new Dictionary<string, FieldDefinition>();
        public Dictionary<string, PropertyDefinition> staticProperties = new Dictionary<string, PropertyDefinition>();
    }

    public class MethodData
    {
        public MethodDefinition definition;
        public MethodInfo       methodInfo;

        public MethodData(MethodDefinition definition, MethodInfo methodInfo)
        {
            this.definition = definition; this.methodInfo = methodInfo;
        }
    }

    public class AssemblyDataBuilder
    {
        /// <summary>
        /// 是否合法，要求baseAssDef中存在的类型和方法签名在newAssDef中必须存在，但newAssDef可以存在新增的类型和方法
        /// </summary>
        public bool isValid { get; private set; }
        public AssemblyData assemblyData { get; private set; } = new AssemblyData();

        private AssemblyDefinition _baseAssDef;
        private AssemblyDefinition _newAssDef;
        private int _patchNo;

        public AssemblyDataBuilder(AssemblyDefinition baseAssDef, AssemblyDefinition newAssDef)
        {
            _baseAssDef = baseAssDef;
            _newAssDef = newAssDef;
            assemblyData.name = _baseAssDef.FullName;
        }

        public bool DoBuild(int patchNo)
        {
            _patchNo = patchNo;
            
            var baseTypes = _baseAssDef.MainModule.Types;
            var newTypes = _newAssDef.MainModule.Types;
            if (baseTypes.Count != newTypes.Count)
            {
                Debug.LogError($"Assembly [{assemblyData.name}]'s type count changed dururing play mode, skiped");
                return false;
            }

            foreach (var t in baseTypes)
                GenTypeInfos(t, assemblyData.baseTypes);

            foreach (var t in newTypes)
                GenTypeInfos(t, assemblyData.newTypes);

            if (!FindAndCheckModifiedMethods())
                return false;

            FillMethodInfoField();
            FixNewAssembly();
            isValid = true;
            return isValid;
        }

        public void GenTypeInfos(TypeDefinition typeDefinition, Dictionary<string, TypeData> types)
        {
            TypeData typeData = new TypeData();
            typeData.definition = typeDefinition;
            types.Add(typeDefinition.ToString(), typeData);

            foreach (var method in typeDefinition.Methods)
            {
                if (method.IsAbstract || !method.HasBody || method.Name.Contains(".cctor")) // 静态构造函数只会被执行一次，hook没有意义
                    continue;

                typeData.methods.Add(method.ToString(), new MethodData(method, null));
            }

            foreach(var field in typeDefinition.Fields)
            {
                if (field.IsStatic)
                    typeData.staticFields.Add(field.ToString(), field);
            }

            foreach(var prop in typeDefinition.Properties)
            {
                if (prop.HasThis)
                    continue;

                var getMethod = prop.GetMethod;
                var setMethod = prop.SetMethod;
                if (getMethod != null && getMethod.HasBody)
                    typeData.methods.Add(getMethod.ToString(), new MethodData(getMethod, null)); // TODO 测试反射是否可以直接通过函数名获取属性的get方法
                if (setMethod != null && setMethod.HasBody)
                    typeData.methods.Add(setMethod.ToString(), new MethodData(setMethod, null));
            }

            foreach(var nest in typeDefinition.NestedTypes)
            {
                GenTypeInfos(nest, types);
            }
        }

        public bool FindAndCheckModifiedMethods()
        {
            // 以 base assembly 为基准检查
            foreach(var kvT in assemblyData.baseTypes)
            {
                string typeName = kvT.Key;
                TypeData baseType = kvT.Value;
                if(!assemblyData.newTypes.TryGetValue(typeName, out TypeData newType))
                {
                    Debug.Log($"can not find type `{typeName}` in new assembly[{assemblyData.name}]");
                    return false;
                }

                var baseMethods = baseType.methods;
                var newMethods = newType.methods;
                foreach (var kvM in baseMethods)
                {
                    string methodName = kvM.Key;
                    MethodData baseMethod = kvM.Value;
                    if(!newMethods.TryGetValue(methodName, out MethodData newMethod))
                    {
                        Debug.Log($"can not find method `{methodName}` in new assembly[{assemblyData.name}]");
                        return false;
                    }

                    var baseIns = baseMethod.definition.Body.Instructions;
                    var newIns = newMethod.definition.Body.Instructions;
                    bool hasModified = false;

                    if (baseIns.Count != newIns.Count)
                        hasModified = true;
                    else
                    {
                        // TODO 通过 method RVA/codeSize 对比，method header 只有两种size，1 or 12
                        var arrBaseIns = baseIns.ToArray();
                        var arrNewIns = newIns.ToArray();
                        for (int l = 0, lmax = arrBaseIns.Length; l < lmax; l++)
                        {
                            if (arrBaseIns[l].ToString() != arrNewIns[l].ToString())
                            {
                                hasModified = true;
                                break;
                            }
                        }
                    }

                    if (hasModified)
                        assemblyData.methodModified.Add(newMethod);
                }
            }
            return true;
        }

        void FillMethodInfoField()
        {
            Assembly ass = (from ass_ in AppDomain.CurrentDomain.GetAssemblies() where ass_.FullName == assemblyData.name select ass_).FirstOrDefault();
            Debug.Assert(ass != null);

            Dictionary<TypeDefinition, Type> dicType = new Dictionary<TypeDefinition, Type>();
            foreach (var md in assemblyData.methodModified)
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
                if (md.methodInfo == null)
                {
                    Debug.LogError($"can not find MethodInfo of [{md.definition.FullName}]");
                }
            }
        }

        void FixNewAssembly()
        {
            // .net 不允许加载同名Assembly，因此需要改名
            _newAssDef.Name.Name = $"{_baseAssDef.Name}_{_patchNo}";
            _newAssDef.MainModule.ModuleReferences.Add(_baseAssDef.MainModule);
            
            // 将发生改变的函数的IL中当前Assembly范围内的外部引用全部指向原始Assembly，以修正静态变量读写并支持其它函数的调试
            foreach(var methodData in assemblyData.methodModified)
            {
                var arrIns= methodData.definition.Body.Instructions.ToArray();
                for(int i = 0, imax = arrIns.Length; i < imax; i++)
                {
                    var ins = arrIns[i];

                }
            }
        }
    }

    class TypeDefComparer<T> : IComparer<T> where T : MemberReference
    {
        public static TypeDefComparer<T> comparer = new TypeDefComparer<T>();

        public int Compare(T x, T y)
        {
            return x.FullName.CompareTo(y.FullName);
        }
    }

}
