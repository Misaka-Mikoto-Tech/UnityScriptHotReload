/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;
using Mono.Cecil;
using System.Linq;
using System.Text;
using Mono.Cecil.Cil;
using SimpleJSON;

using static AssemblyPatcher.Utils;
using System.Security.Permissions;
using SecurityAction = System.Security.Permissions.SecurityAction;
using OpCodes = Mono.Cecil.Cil.OpCodes;

namespace AssemblyPatcher;

public class AssemblyData
{
    public string                           name;
    public AssemblyDefinition               baseAssDef;
    public AssemblyDefinition               newAssDef;
    public Assembly                         assembly;
    public Dictionary<string, TypeData>     baseTypes       = new Dictionary<string, TypeData>();
    public Dictionary<string, TypeData>     newTypes        = new Dictionary<string, TypeData>();
    public Dictionary<string, TypeData>     addedTypes = new Dictionary<string, TypeData>(); // 新增类型，不包括自动生成的 lambda表达式类
    public Dictionary<string, MethodData>   allBaseMethods    = new Dictionary<string, MethodData>(); // 用于快速搜索
    public Dictionary<string, HookedMethodInfo>   methodsNeedHook  = new Dictionary<string, HookedMethodInfo>();
    public Dictionary<Document, List<MethodData>> doc2methodsOfNew = new Dictionary<Document, List<MethodData>>(); // newTypes 中 doc 与 method的映射
}

public class TypeData
{
    // 不会记录 GenericInstanceType, FixMethod 时遇到会动态创建并替换
    public TypeDefinition   definition;
    public TypeDefinition   definitionMatched; // 如果是新的dll中的类型, 此字段为BaseDll中的同名类型，否则此字段为null

    public bool             isLambdaStaticType; // 名字为 `<>` 的类型
    public TypeData         parent;
    public TypeData         childLambdaStaticType; // TypeName/<>c

    public Dictionary<string, MethodData>           methods = new Dictionary<string, MethodData>();
    public Dictionary<string, FieldDefinition>      fields = new Dictionary<string, FieldDefinition>();
}

public class MethodData
{
    // 不会记录 GenericInstanceMethod, FixMethod 时遇到会动态创建并替换
    public TypeData         typeData;
    public MethodDefinition definition;
    public MethodDefinition definitionMatched; // 如果是新的dll中的方法, 此字段为BaseDll中的同名方法，否则此字段为null
    public MethodBase       methodInfo;
    public bool             isLambda;
    public bool             ilChanged;
    public Document         document;

    public MethodData(TypeData typeData, MethodDefinition definition, MethodInfo methodInfo, bool isLambda)
    {
        this.typeData = typeData; this.definition = definition; this.methodInfo = methodInfo; this.isLambda = isLambda;
        this.document = GetDocOfMethod(definition);
    }

    public JSONNode ToJsonNode()
    {
        string moduleName = typeData.definition.Module.Name;

        JSONObject ret = new JSONObject();
        ret["name"] = methodInfo.Name;
        ret["type"] = GetRuntimeTypeName(methodInfo.DeclaringType, methodInfo.ContainsGenericParameters);
        ret["assembly"] = typeData.definition.Module.Name;
        ret["isConstructor"] = methodInfo.IsConstructor;
        ret["isGeneric"] = methodInfo.ContainsGenericParameters;
        ret["isPublic"] = methodInfo.IsPublic;
        ret["isStatic"] = methodInfo.IsStatic;
        ret["isLambda"] = isLambda;
        ret["ilChanged"] = ilChanged;
        ret["document"] = document.Url.Substring(Environment.CurrentDirectory.Length + 1);

        if (!methodInfo.IsConstructor)
            ret["returnType"] = (methodInfo as MethodInfo).ReturnType.ToString();

        JSONArray paraArr = new JSONArray();
        ret.Add("paramTypes", paraArr);
        var paras = methodInfo.GetParameters();
        for(int i = 0, imax = paras.Length; i < imax; i++)
        {
            paraArr[i] = GetRuntimeTypeName(paras[i].ParameterType, methodInfo.ContainsGenericParameters);
        }

        return ret;
    }
}

public class HookedMethodInfo
{
    public MethodData baseMethod;
    public MethodData newMethod;
    public bool ilChanged;

    public HookedMethodInfo(MethodData baseMethod, MethodData newMethod, bool ilChanged)
    {
        this.baseMethod = baseMethod; this.newMethod = newMethod; this.ilChanged = ilChanged;
    }
}

public class MethodFixStatus
{
    public bool needHook;
    public bool ilFixed;
}

/// <summary>
/// 程序集构建器
/// </summary>
public class AssemblyDataBuilder
{
    /// <summary>
    /// 是否合法，要求baseAssDef中存在的类型和方法签名在newAssDef中必须存在，但newAssDef可以存在新增的类型和方法
    /// </summary>
    public bool isValid { get; private set; }
    public AssemblyData assemblyData { get; private set; } = new AssemblyData();

    private AssemblyDefinition  _baseAssDef;
    private AssemblyDefinition  _newAssDef;
    private MethodPatcher       _methodPatcher;
    private int                 _patchNo;

    public AssemblyDataBuilder(AssemblyDefinition baseAssDef, AssemblyDefinition newAssDef)
    {
        _baseAssDef = baseAssDef;
        _newAssDef = newAssDef;
        assemblyData.name = _baseAssDef.FullName;
        assemblyData.baseAssDef = baseAssDef;
        assemblyData.newAssDef = newAssDef;
        _methodPatcher = new MethodPatcher(assemblyData);
    }

    public bool DoBuild(int patchNo)
    {
        _patchNo = patchNo;

        assemblyData.baseTypes.Clear ();
        assemblyData.newTypes.Clear ();
        assemblyData.addedTypes.Clear();
        assemblyData.allBaseMethods.Clear ();
        assemblyData.methodsNeedHook.Clear ();
        
        var baseTypes = _baseAssDef.MainModule.Types;
        var newTypes = _newAssDef.MainModule.Types; // 不包括 NestedClass

        foreach (var t in baseTypes)
            GenTypeInfos(t, null, assemblyData.baseTypes);

        foreach (var t in newTypes)
            GenTypeInfos(t, null, assemblyData.newTypes);

        // 全局处理类型和方法
        foreach(var kvT in assemblyData.newTypes)
        {
            string typeName = kvT.Key;
            TypeData typeData = kvT.Value;

            if (!assemblyData.baseTypes.ContainsKey(typeName))
            {
                assemblyData.addedTypes.Add(typeName, typeData);
                continue;
            }

            if (IsLambdaStaticType(typeName))
                continue;

            foreach(var kvM in typeData.methods)
            {
                var methodData = kvM.Value;
                var doc = methodData.document;
                if (doc == null || methodData.isLambda)
                    continue;

                if (!assemblyData.doc2methodsOfNew.TryGetValue(doc, out List<MethodData> lst))
                {
                    lst = new List<MethodData>();
                    assemblyData.doc2methodsOfNew.Add(doc, lst);
                }

                lst.Add(kvM.Value);
            }
        }

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
            // 静态构造函数不允许被hook，否则静态构造函数会被自动再次执行导致数据错误
            if (method.IsAbstract || !method.HasBody || method.Name.Contains(".cctor"))
                continue;

            // property getter, setter 也包含在内，且 IsGetter, IsSetter 字段会设置为 true, 因此无需单独遍历 properties
            var data = new MethodData(typeData, method, null, IsLambdaMethod(method));
            typeData.methods.Add(method.ToString(), data);
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

            // 新增虚函数是不允许，会导致虚表发生变化
            foreach(var kvM in newType.methods)
            {
                if(!baseType.methods.ContainsKey(kvM.Key))
                {
                    if(kvM.Value.definition.IsVirtual)
                    {
                        Debug.LogError($"add virtual method is not allowd:{kvM.Key}");
                        return false;
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
            // lambda 表达式在新增类函数后自动编号会发生变化，并且我们不打算hook，因此跳过
            if (IsLambdaStaticType(typeName))
                continue;

            if (typeName == "UnitySourceGeneratedAssemblyMonoScriptTypes") // 这个貌似是 Unity2022 记录类型信息的，处理没有意义
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
                bool ilChanged = false;

                if (baseIns.Count != newIns.Count)
                    ilChanged = true;
                else
                {
                    /*
                     * TODO 通过 method RVA/codeSize 对比，method header 只有两种size，1 or 12
                     * 2022-12-23: 经测试不能直接对比 method body 的二进制，原因是类的成员变量RID每次编译会不一致，导致 ldfld 之类的指令引用的编号发生变化
                     */
                    var arrBaseIns = baseIns.ToArray();
                    var arrNewIns = newIns.ToArray();
                    for (int l = 0, lmax = arrBaseIns.Length; l < lmax; l++)
                    {
                        if (arrBaseIns[l].ToString() != arrNewIns[l].ToString())
                        {
                            ilChanged = true;
                            break;
                        }
                    }
                }

                if (ilChanged)
                {
                    baseMethod.ilChanged = true;
                    newMethod.ilChanged = true;
                    assemblyData.methodsNeedHook.Add(methodName, new HookedMethodInfo(baseMethod, newMethod, true));
                }
            }
        }

        // il发生改变的函数所在文件的其它所有原dll中存在的函数均添加到 modified 列表，以全部使用新的pdb(它们的行号可能发生了改变，不执行hook会导致断点失效)
        HashSet<Document> docChanged = new HashSet<Document>();
        foreach(var kv in assemblyData.methodsNeedHook)
        {
            var doc = kv.Value.newMethod.document;
            if(doc != null)
                docChanged.Add(doc);
        }

        foreach(var doc in docChanged)
        {
            if(assemblyData.doc2methodsOfNew.TryGetValue(doc, out List<MethodData> lstNewMethods))
            {
                foreach(var newMethod in lstNewMethods)
                {
                    if (newMethod.isLambda || newMethod.definition.Name == ".cctor" || IsLambdaStaticType(newMethod.definition.DeclaringType))
                        continue;

                    var name = newMethod.definition.FullName;
                    if (assemblyData.allBaseMethods.TryGetValue(name, out MethodData baseMethod))
                        assemblyData.methodsNeedHook.TryAdd(name, new HookedMethodInfo(baseMethod, newMethod, false));
                }
            }
        }

        return true;
    }

    void FillMethodInfoField()
    {
        Assembly ass = (from ass_ in AppDomain.CurrentDomain.GetAssemblies() where ass_.FullName == assemblyData.name select ass_).FirstOrDefault();
        Debug.Assert(ass != null);

        Dictionary<TypeDefinition, Type> dicType = new Dictionary<TypeDefinition, Type>();
        foreach (var kv in assemblyData.methodsNeedHook)
        {
            MethodData methodData = kv.Value.baseMethod;
            Type t;
            MethodDefinition definition = methodData.definition;
            if (!dicType.TryGetValue(definition.DeclaringType, out t))
            {
                t = ass.GetType(definition.DeclaringType.FullName.Replace('/', '+'));
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
        // .net 不允许加载同名Assembly，因此需要改名
        _newAssDef.Name.Name = string.Format(InputArgs.Instance.patchAssemblyNameFmt, _baseAssDef.Name.Name, _patchNo);
        {
            var ver = _newAssDef.Name.Version;
            ver = new Version(ver.Major, ver.Minor, ver.Build, ver.Revision + _patchNo);
            _newAssDef.Name.Version = ver;
        }

        _newAssDef.MainModule.Name = _newAssDef.Name.Name + ".dll";
        _newAssDef.MainModule.Mvid = Guid.NewGuid();
        
        _newAssDef.MainModule.ModuleReferences.Add(_baseAssDef.MainModule);

        {// 给Assembly添加Attribute以允许IL访问外部类的私有字段
            // IgnoresAccessChecksTo(AssemblyName)
            {
                var newAttr = _newAssDef.MainModule.ImportReference(typeof(IgnoresAccessChecksToAttribute).GetConstructor(new Type[] { typeof(string) }));
                var attr = new CustomAttribute(newAttr);
                attr.ConstructorArguments.Add(new CustomAttributeArgument(_newAssDef.MainModule.ImportReference(typeof(string)), _baseAssDef.Name.Name));
                _newAssDef.CustomAttributes.Add(attr);
            }
        }

        Dictionary<string, MethodData> methodsToFix =
            new Dictionary<string, MethodData> (from data in assemblyData.methodsNeedHook select KeyValuePair.Create(data.Key, data.Value.newMethod));

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

        // 新增类的函数全部需要被修正，包括静态构造函数，因为其静态构造函数是第一次调用，需要Fix而不是Remove
        foreach(var kvT in assemblyData.addedTypes)
        {
            foreach(var kvM in kvT.Value.methods)
            {
                if (!methodsToFix.ContainsKey(kvM.Key))
                    methodsToFix.Add(kvM.Key, kvM.Value);
            }
        }

        var processed = new Dictionary<MethodDefinition, MethodFixStatus>();
        foreach (var kv in methodsToFix)
        {
            _methodPatcher.PatchMethod(kv.Value.definition, processed, 0);
        }


        // 已存在类的静态构造函数需要清空，防止被二次调用
        if (processed.Count > 0)
        {
            var fixedType = new HashSet<TypeDefinition>();
            foreach(var kv in processed)
            {
                var status = kv.Value;
                if (status.ilFixed || status.needHook)
                    fixedType.Add(kv.Key.DeclaringType);
            }

            if(fixedType.Count > 0)
            {
                var constructors = new List<MethodDefinition>();
                var lambdaWrapperBackend = InputArgs.Instance.lambdaWrapperBackend;
                foreach (var tdef in fixedType)
                {
                    if (tdef.FullName.EndsWith(lambdaWrapperBackend, StringComparison.Ordinal))
                        continue;

                    // 新定义的类型静态构造函数即使执行也是第一次执行，因此逻辑只能修正不能移除
                    if (assemblyData.addedTypes.ContainsKey(tdef.FullName))
                        continue;

                    foreach(var mdef in tdef.Methods)
                    {
                        if (mdef.IsConstructor && mdef.IsStatic && mdef.HasBody)
                            constructors.Add(mdef);
                    }
                }
                RemoveStaticConstructorsBody(constructors);
            }

#if SCRIPT_PATCH_DEBUG
            StringBuilder sb = new StringBuilder();
            foreach (var kv in processed)
            {
                bool ilChanged = false;
                if (assemblyData.allBaseMethods.TryGetValue(kv.Key.FullName, out MethodData methodData))
                    ilChanged = methodData.ilChanged;

                sb.AppendLine(kv.Key + (ilChanged ? " [Changed]" : "") + (kv.Value.needHook ? " [Hook]" : "") + (kv.Value.ilFixed ? " [Fix]" : ""));
            }

            Debug.Log($"<color=yellow>Patch Methods of `{_baseAssDef.Name.Name}`: </color>{sb}");
#endif
        }


    }

    /// <summary>
    /// 修正被Hook或者被Fix的类型的静态构造函数，将它们改为直接返回的空函数, 否则它们会执行两遍
    /// </summary>
    /// <param name="constructors"></param>
    /// <remarks>新增类的静态构造函数由于是第一次执行，因此不能清空函数体，只能修正</remarks>
    void RemoveStaticConstructorsBody(List<MethodDefinition> constructors)
    {
        foreach(var ctor in constructors)
        {
            if (ctor.Name != ".cctor" || !ctor.HasBody)
                continue;

            var il = ctor.Body.GetILProcessor();
            il.Clear();
            il.Append(Instruction.Create(OpCodes.Ret));
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

