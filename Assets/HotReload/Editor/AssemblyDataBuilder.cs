using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;
using System.Reflection.Emit;
using Mono;
using Mono.Cecil;
using System.Linq;
using System.Text;
using Mono.Cecil.Cil;
using UnityEngine;
using UnityEditor;

using static ScriptHotReload.HotReloadUtils;
using static ScriptHotReload.HotReloadConfig;

namespace ScriptHotReload
{
    public class AssemblyData
    {
        public string       name;
        public Assembly     assembly;
        public Dictionary<string, TypeData>     baseTypes       = new Dictionary<string, TypeData>();
        public Dictionary<string, TypeData>     newTypes        = new Dictionary<string, TypeData>();
        public Dictionary<string, MethodData>   allBaseMethods    = new Dictionary<string, MethodData>(); // 用于快速搜索
        public List<MethodData>                 methodModified  = new List<MethodData>(); // new method data
    }

    public class TypeData
    {
        public TypeDefinition   definition;
        public Type             type;
        public Dictionary<string, MethodData>           methods = new Dictionary<string, MethodData>();
        public Dictionary<string, FieldDefinition>      fields = new Dictionary<string, FieldDefinition>();
        public Dictionary<string, PropertyDefinition>   staticProperties = new Dictionary<string, PropertyDefinition>();
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

            assemblyData.baseTypes.Clear ();
            assemblyData.newTypes.Clear ();
            assemblyData.allBaseMethods.Clear ();
            assemblyData.methodModified.Clear ();
            
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

            FillAllBaseMethods();

            if (!CheckTypesLayout())
                return false;

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
            string sig = typeDefinition.ToString();
            types.Add(sig, typeData);

            foreach (var method in typeDefinition.Methods)
            {
                if (method.IsAbstract || !method.HasBody || method.Name.Contains(".cctor")) // 静态构造函数只会被执行一次，hook没有意义
                    continue;

                // property getter, setter 也包含在内，且 IsGetter, IsSetter 字段会设置为 true, 因此无需单独遍历 properties
                typeData.methods.Add(method.ToString(), new MethodData(method, null));
            }

            foreach(var field in typeDefinition.Fields)
            {
                typeData.fields.Add(field.ToString(), field);
            }

            foreach(var nest in typeDefinition.NestedTypes)
            {
                GenTypeInfos(nest, types);
            }
        }

        void FillAllBaseMethods()
        {
            foreach(var kvT in assemblyData.baseTypes)
            {
                foreach(var t in kvT.Value.methods)
                {
                    assemblyData.allBaseMethods.Add (t.Key, t.Value);
                }
            }
        }

        /// <summary>
        /// hotreload时要求已存在类型的内存布局严格一致
        /// </summary>
        /// <returns></returns>
        public bool CheckTypesLayout()
        {
            foreach (var kvT in assemblyData.baseTypes)
            {
                string typeName = kvT.Key;
                TypeData baseType = kvT.Value;
                if (!assemblyData.newTypes.TryGetValue(typeName, out TypeData newType))
                {
                    Debug.LogError($"can not find type `{typeName}` in new assembly[{assemblyData.name}]");
                    return false;
                }

                bool isLambdaBackend = typeName.EndsWith(kLambdaWrapperBackend, StringComparison.Ordinal);

                var baseFields = baseType.definition.Fields.ToArray();
                var newFields = newType.definition.Fields.ToArray();
                if(baseFields.Length != newFields.Length)
                {
                    Debug.LogError($"field count changed of type:{typeName}");
                    return false;
                }
                for(int i = 0, imax = baseFields.Length; i < imax; i++)
                {
                    var baseField = baseFields[i];
                    var newField = newFields[i];
                    if(!isLambdaBackend)
                    {
                        if (baseField.ToString() != newField.ToString())
                        {
                            Debug.LogError($"field `{baseField}` changed of type:{typeName}");
                            return false;
                        }
                    }
                    else
                    {
                        // lambda wrapper 自动生成的代码只判断类型（因为其编号是类函数总数量，新增函数后编号会变化）
                        if(baseField.FieldType.ToString() != newField.FieldType.ToString())
                        {
                            Debug.LogError($"lambda expression `{baseField}` signature changed of type:{typeName}");
                            return false;
                        }
                        else
                        {
                            // 修正 newAssembly 中的lambda映射关系

                        }
                    }
                }
            }
            return true;
        }


        public bool FindAndCheckModifiedMethods()
        {
            // 以 base assembly 为基准检查
            foreach(var kvT in assemblyData.baseTypes)
            {
                string typeName = kvT.Key;
                // TODO lambda 表达式在新增类函数后自动编号会发生变化，暂时没有合适的匹配方法(如果不打算新增函数可以注释掉此判断语句)
                if (typeName.EndsWith(kLambdaWrapperBackend, StringComparison.Ordinal))
                    continue;

                TypeData baseType = kvT.Value;
                if(!assemblyData.newTypes.TryGetValue(typeName, out TypeData newType))
                {
                    Debug.LogError($"can not find type `{typeName}` in new assembly[{assemblyData.name}]");
                    return false;
                }

                TypeDefinition baseDef = baseType.definition;
                TypeDefinition newDef = newType.definition;
                if(baseDef.IsClass != newDef.IsClass || baseDef.IsEnum != newDef.IsEnum || baseDef.IsInterface != newDef.IsInterface)
                {
                    Debug.LogError($"signature of type `{typeName}` has changed in new assembly[{assemblyData.name}]");
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
                        Debug.LogError($"can not find method `{methodName}` in new assembly[{assemblyData.name}]");
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
            _newAssDef.Name.Name = $"{_baseAssDef.Name.Name}_{_patchNo}";
            _newAssDef.MainModule.ModuleReferences.Add(_baseAssDef.MainModule);

            HashSet<MethodDefinition> processed = new HashSet<MethodDefinition>();
            foreach (var methodData in assemblyData.methodModified)
            {
                FixMethod(methodData.definition, processed);
            }
        }

        /// <summary>
        /// 将发生改变的函数的IL中当前Assembly范围内的外部引用全部指向原始Assembly，以修正静态变量读写和函数调用
        /// </summary>
        /// <param name="definition"></param>
        /// <param name="processed"></param>
        void FixMethod(MethodDefinition definition, HashSet<MethodDefinition> processed)
        {
            if (processed.Contains(definition))
                return;
            else
                processed.Add(definition);

            // 参数和返回值由于之前已经检查过名称是否一致，因此是二进制兼容的，可以不进行检查
            var arrIns = definition.Body.Instructions.ToArray();
            var ilProcessor = definition.Body.GetILProcessor ();

            for (int i = 0, imax = arrIns.Length; i < imax; i++)
            {
                Instruction ins = arrIns[i];
                do
                {
                    /*
                     * Field 有两种类型: FieldReference/FieldDefinition, 经过研究发现 FieldReference 指向了外部 Assembly, 而 FieldDefinition 则是当前 Assembly 内定义的
                     * 因此我们只需要检查 FieldDefinition, 然后把它们替换成同名的 FieldReference
                     * Method 同理
                     */
                    if (ins.Operand is FieldDefinition)
                    {
                        var fieldDef = ins.Operand as FieldDefinition;
                        if (!fieldDef.IsStatic) break;
                        if (fieldDef.DeclaringType.Module != _newAssDef.MainModule) break;

                        bool isLambda = IsLambdaBackend(fieldDef.DeclaringType);
                        if (!isLambda && assemblyData.baseTypes.TryGetValue (fieldDef.FullName, out TypeData baseTypeData))
                        {
                            _newAssDef.MainModule.ImportReference(baseTypeData.definition);
                            var newIns = Instruction.Create (ins.OpCode, baseTypeData.definition);
                            ilProcessor.Replace (ins, newIns);
                        } else
                        {
                            // 新定义的类型或者lambda, 可以使用新的Assembly内的定义, 但需要递归修正其中的方法
                            if(assemblyData.newTypes.TryGetValue(definition.ToString(), out TypeData typeData))
                            {
                                foreach(var kv in typeData.methods)
                                {
                                    FixMethod(kv.Value.definition, processed);
                                }
                            }
                        }
                    }
                    else if(ins.Operand is MethodDefinition)
                    {
                        var methodDef = ins.Operand as MethodDefinition;
                        bool isLambda = IsLambdaBackend(methodDef.DeclaringType);
                        if (!isLambda && assemblyData.allBaseMethods.TryGetValue(methodDef.ToString(), out MethodData baseMethodDef))
                        {
                            _newAssDef.MainModule.ImportReference(baseMethodDef.definition.DeclaringType);
                            var newIns = Instruction.Create (ins.OpCode, baseMethodDef.definition);
                            ilProcessor.Replace (ins, newIns);
                        }
                        else // 这是新定义的方法或者lambda表达式，需要递归检查
                        {
                            FixMethod (methodDef, processed); // TODO 当object由非hook代码创建时调用新添加的虚方法可能有问题
                        }
                    }
                }
                while (false) ;
                
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
