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

using static ScriptHotReload.HotReloadUtils;
using static ScriptHotReload.HotReloadConfig;
using System.Security.Permissions;
using SecurityAction = System.Security.Permissions.SecurityAction;
using Unity.VisualScripting;

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
#else
class Debug {
	public static void Log (string msg)
	{
		Console.WriteLine (msg);
	}

	public static void LogError (string msg)
	{
		Console.WriteLine (msg);
	}

	public static void Assert(bool condition)
	{
		System.Diagnostics.Debug.Assert (condition);
	}
}
#endif

/*
 * 安全相关特性，如果生成的patch dll出现 xx 字段/方法 `FieldAccessException` 异常，可以根据当前运行时开放下列其中之一
 * 
 * [assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)] // for mono and .net framework < 4.0
 * [assembly: IgnoresAccessChecksTo("TestDll")] // for .net core, and pls modify assembly name
 */

namespace ScriptHotReload
{
    public class AssemblyData
    {
        public string       name;
        public Assembly     assembly;
        public Dictionary<string, TypeData>     baseTypes       = new Dictionary<string, TypeData>();
        public Dictionary<string, TypeData>     newTypes        = new Dictionary<string, TypeData>();
        public Dictionary<string, MethodData>   allBaseMethods    = new Dictionary<string, MethodData>(); // 用于快速搜索
        public Dictionary<string, MethodData>   methodModified  = new Dictionary<string, MethodData>(); // new method data
    }

    public class TypeData
    {
        public TypeDefinition   definition;
        public Type             type;

        public bool             isLambdaStaticType; // 名字为 `<>` 的类型
        public TypeData         parent;
        public TypeData         childLambdaStaticType; // TypeName/<>c

        public Dictionary<string, MethodData>           methods = new Dictionary<string, MethodData>();
        public Dictionary<string, FieldDefinition>      fields = new Dictionary<string, FieldDefinition>();
    }

    public class MethodData
    {
        public TypeData         typeData;
        public MethodDefinition definition;
        public MethodInfo       methodInfo;
        public bool             isLambda;

        public MethodData(TypeData typeData, MethodDefinition definition, MethodInfo methodInfo, bool isLambda)
        {
            this.typeData = typeData; this.definition = definition; this.methodInfo = methodInfo; this.isLambda = isLambda;
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

#if SCRIPT_PATCH_DEBUG
        class DebugMsg
        {
            public string msg;
        }
        List<DebugMsg> lstDebug = new List<DebugMsg>();
#endif

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

            foreach (var t in baseTypes)
                GenTypeInfos(t, null, assemblyData.baseTypes);

            foreach (var t in newTypes)
                GenTypeInfos(t, null, assemblyData.newTypes);

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

        public void GenTypeInfos(TypeDefinition typeDefinition, TypeData parent, Dictionary<string, TypeData> types)
        {
            TypeData typeData = new TypeData();
            typeData.definition = typeDefinition;
            typeData.parent = parent;
            
            string sig = typeDefinition.ToString();
            typeData.isLambdaStaticType = IsLambdaStaticType(sig);
            types.Add(sig, typeData);

            if (typeData.isLambdaStaticType)
                parent.childLambdaStaticType = typeData;

            foreach (var method in typeDefinition.Methods)
            {
                if (method.IsAbstract || !method.HasBody || method.Name.Contains(".cctor")) // 静态构造函数只会被执行一次，hook没有意义
                    continue;

                // property getter, setter 也包含在内，且 IsGetter, IsSetter 字段会设置为 true, 因此无需单独遍历 properties
                typeData.methods.Add(method.ToString(), new MethodData(typeData, method, null, IsLambdaMethod(method)));
            }

            foreach(var field in typeDefinition.Fields)
            {
                typeData.fields.Add(field.ToString(), field);
            }

            foreach(var nest in typeDefinition.NestedTypes)
            {
                GenTypeInfos(nest, typeData, types);
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

                if(IsLambdaStaticType(typeName))
                    continue; // 名称为 `<>` 的存储不包含外部引用的lambda函数的类型，内部只有静态字段, 由于我们不对其hook，因此内存布局不予考虑

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
                    if (baseField.ToString() != newField.ToString())
                    {
                        Debug.LogError($"field `{baseField}` changed of type:{typeName}");
                        return false;
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
                // lambda 表达式在新增类函数后自动编号会发生变化，并且我们不打算hook，因此跳过
                if (IsLambdaStaticType(typeName))
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
                    if (IsLambdaMethod(baseMethod.definition)) // <MethodName>b_xx_y
                        continue;

                    if (!newMethods.TryGetValue(methodName, out MethodData newMethod))
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
                        assemblyData.methodModified.Add(methodName, newMethod);
                }
            }

            return true;
        }

        void FillMethodInfoField()
        {
            Assembly ass = (from ass_ in AppDomain.CurrentDomain.GetAssemblies() where ass_.FullName == assemblyData.name select ass_).FirstOrDefault();
            Debug.Assert(ass != null);

            Dictionary<TypeDefinition, Type> dicType = new Dictionary<TypeDefinition, Type>();
            foreach (var kv in assemblyData.methodModified)
            {
                MethodData methodData = kv.Value;
                Type t;
                MethodDefinition definition = methodData.definition;
                if (!dicType.TryGetValue(definition.DeclaringType, out t))
                {
                    t = ass.GetType(definition.DeclaringType.FullName);
                    Debug.Assert(t != null);
                    dicType.Add(definition.DeclaringType, t);
                }
                methodData.methodInfo = GetMethodInfoSlow(t, definition);
                if (methodData.methodInfo == null)
                {
                    Debug.LogError($"can not find MethodInfo of [{methodData.definition.FullName}]");
                }
            }
        }

        
        void FixNewAssembly()
        {
#if SCRIPT_PATCH_DEBUG
            lstDebug.Clear();
#endif
            // .net 不允许加载同名Assembly，因此需要改名
            _newAssDef.Name.Name = string.Format(kPatchAssemblyName, _baseAssDef.Name.Name, _patchNo);
            {
                var ver = _newAssDef.Name.Version;
                ver = new Version(ver.Major, ver.Minor, ver.Build, ver.Revision + _patchNo);
                _newAssDef.Name.Version = ver;
            }

            _newAssDef.MainModule.Name = _newAssDef.Name.Name + ".dll";
            _newAssDef.MainModule.Mvid = Guid.NewGuid();
            
            _newAssDef.MainModule.ModuleReferences.Add(_baseAssDef.MainModule);

            {// 给Assembly添加Attribute以允许IL访问外部类的私有字段
                using(var editorAssemDef = AssemblyDefinition.ReadAssembly(MethodBase.GetCurrentMethod().DeclaringType.Assembly.Location))
                {
                    // 使用当前 editor dll的 security attributes 替换目标数据（构造这些数据太复杂，editor dll 我们可以提前定义模板）
                    _newAssDef.SecurityDeclarations.Clear();
                    _newAssDef.SecurityDeclarations.AddRange(editorAssemDef.SecurityDeclarations);
                    // TODO for .net core, modify assembly name of `IgnoresAccessChecksTo` with code
                }
            }

            Dictionary<string, MethodData> methodsToFix = new Dictionary<string, MethodData>(assemblyData.methodModified);

            /*
             * 被修改的函数所在的类型的 lambda 成员函数及 lambda static type 子类里的所有方法也一并修正，避免 lambda 表达式内的外部调用跑出范围
             * FixMethod 解析IL时遇到lambda表达式时也会递归，但无法递归到IL未访问的 lambda 表达式，因此此处将其提前加入列表
             */
            var oriMethods = methodsToFix.Values.ToArray();
            foreach(var method in oriMethods)
            {
                TypeData typeData = method.typeData;
                foreach(var kvM in typeData.methods)
                {
                    if(IsLambdaMethod(kvM.Value.definition))
						if(!methodsToFix.ContainsKey(kvM.Key))
							methodsToFix.Add(kvM.Key, kvM.Value);
                }

                if(typeData.childLambdaStaticType != null)
                {
                    foreach (var kvM in typeData.childLambdaStaticType.methods)
                    {
						if (!methodsToFix.ContainsKey (kvM.Key))
							methodsToFix.Add(kvM.Key, kvM.Value);
                    }
                }
            }

            HashSet<MethodDefinition> processed = new HashSet<MethodDefinition>();
            foreach (var kv in methodsToFix)
            {
                FixMethod(kv.Value.definition, processed, 0);
            }

#if SCRIPT_PATCH_DEBUG
            if(lstDebug.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                foreach (var msg in lstDebug)
                    sb.AppendLine(msg.msg);
                Debug.Log($"<color=yellow>Patch Methods of `{_baseAssDef.Name.Name}`: </color>{sb.ToString()}");
            }
                
#endif
        }

        /// <summary>
        /// 将发生改变的函数的IL中当前Assembly范围内的外部引用全部指向原始Assembly，以修正静态变量读写和函数调用
        /// </summary>
        /// <param name="definition"></param>
        /// <param name="processed"></param>
        void FixMethod(MethodDefinition definition, HashSet<MethodDefinition> processed, int depth)
        {
#if SCRIPT_PATCH_DEBUG
            bool hasFixed = false;
            var dbgMsg = new DebugMsg();
            if (processed.Contains(definition))
                return;
            else
            {
                processed.Add(definition);

                var sig = definition.ToString();
                if (assemblyData.methodModified.ContainsKey(sig))
                    dbgMsg.msg = sig + "  [Hook]";
                else
                    dbgMsg.msg = sig;

                lstDebug.Add(dbgMsg);
            }
#else
            if (processed.Contains(definition))
                return;
            else
                processed.Add(definition);
#endif

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
                     * Method 同理, 但 lambda 表达式不进行替换，而是递归修正函数
                     */
                    if (ins.Operand is FieldDefinition)
                    {
                        var fieldDef = ins.Operand as FieldDefinition;
                        var fieldType = fieldDef.DeclaringType;
                        if (fieldType.Module != _newAssDef.MainModule) break;

                        bool isLambda = IsLambdaStaticType(fieldType);
                        if (!isLambda && assemblyData.baseTypes.TryGetValue (fieldType.FullName, out TypeData baseTypeData))
                        {
							if (baseTypeData.fields.TryGetValue (fieldDef.FullName, out FieldDefinition baseFieldDef))
							{
								var fieldRef = _newAssDef.MainModule.ImportReference (baseFieldDef);
								var newIns = Instruction.Create (ins.OpCode, fieldRef);
								ilProcessor.Replace (ins, newIns);
#if SCRIPT_PATCH_DEBUG
                                hasFixed = true;
#endif
                            } else
								throw new Exception ($"can not find field {fieldDef.FullName} in base dll");
                        } else
                        {
                            // 新定义的类型或者lambda, 可以使用新的Assembly内的定义, 但需要递归修正其中的方法
                            if(assemblyData.newTypes.TryGetValue(fieldType.ToString(), out TypeData typeData))
                            {
                                foreach(var kv in typeData.methods)
                                {
                                    FixMethod(kv.Value.definition, processed, depth + 1);
                                }
                            }
                        }
                    }
                    else if(ins.Operand is MethodDefinition)
                    {
                        var methodDef = ins.Operand as MethodDefinition;
						bool isLambda = IsLambdaMethod (methodDef);
                        if (!isLambda && assemblyData.allBaseMethods.TryGetValue(methodDef.ToString(), out MethodData baseMethodDef))
                        {
                            var reference = _newAssDef.MainModule.ImportReference(baseMethodDef.definition);
                            var newIns = Instruction.Create (ins.OpCode, reference);
                            ilProcessor.Replace (ins, newIns);
#if SCRIPT_PATCH_DEBUG
                            hasFixed = true;
#endif
                            if(depth < 1)
                            {
                                /*
                                 * 被Hook的函数被调用时，即使IP已跳转到新的地址，但VM还是认为当前的函数栈位于原函数内，其Assembly也识别为原始dll
                                 * 因此其内直接调用的函数无论是否发生改变均需要执行修正
                                 */
                                FixMethod(methodDef, processed, depth + 1);
                            }
                        }
                        else // 这是新定义的方法或者lambda表达式，需要递归修正
                        {
                            FixMethod (methodDef, processed, depth + 1); // TODO 当object由非hook代码创建时调用新添加的虚方法可能有问题
                        }
                    }
                }
                while (false);
                
            }

#if SCRIPT_PATCH_DEBUG
            if (hasFixed)
                dbgMsg.msg += " [Fix]";
#endif
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
