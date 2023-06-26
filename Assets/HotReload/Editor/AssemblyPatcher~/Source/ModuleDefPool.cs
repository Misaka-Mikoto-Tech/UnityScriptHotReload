using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using NHibernate.Mapping;
using SimpleJSON;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TypeDef = dnlib.DotNet.TypeDef;

namespace AssemblyPatcher;

/// <summary>
/// ModuleDef 池，避免多次Load同一个Module
/// </summary>
public static class ModuleDefPool
{
    public static ModuleContext ctx { get; private set; }
    static Dictionary<string, Assembly> _allAssemblies = new Dictionary<string, Assembly>();
    static Dictionary<string, ModuleDefData> _moduleDatas = new Dictionary<string, ModuleDefData>();

    static object _locker = new object();

    static ModuleDefPool()
    {
        CreateCtx();
    }

    /// <summary>
    /// 根据 Module 名字查找相应信息，名字不包含扩展名
    /// </summary>
    /// <param name="moduleName"></param>
    /// <returns></returns>
    public static ModuleDefData GetModuleData(string moduleName)
    {
        ModuleDefData ret;
        lock(_locker)
        {
            _moduleDatas.TryGetValue(moduleName, out ret);
        }

        if(ret is null)
        {
            ret = RegisterModuleData(moduleName);
        }

        return ret;
    }

    static ModuleDefData RegisterModuleData(string moduleName)
    {
        ModuleDefData ret;
        lock (_locker)
        {
            if (_moduleDatas.TryGetValue(moduleName, out ret))
                return ret;
        }

        string fullPath = GlobalConfig.Instance.assemblyPathes[moduleName];

        ret = new ModuleDefData() { name = moduleName };
        ret.moduleDef = ModuleDefMD.Load(fullPath, ctx);
        (ret.moduleDef.Context.AssemblyResolver as AssemblyResolver).AddToCache(ret.moduleDef);

        ret.assRef = new AssemblyRefUser(ret.moduleDef.Assembly);
        ret.moduleRef = new ModuleRefUser(ret.moduleDef);

        ret.types = new Dictionary<string, TypeData>();
        var topTypes = ret.moduleDef.Types; // 不包含 NestedType
        foreach (var t in topTypes)
            GenTypeInfos(t, null, ret.types);

        ret.allMethods = new Dictionary<string, MethodData>();
        ret.allMembers = new Dictionary<string, IMemberRef>();
        foreach(var (_, typeData) in ret.types)
        {
            foreach(var (methodName, methodData) in typeData.methods)
            {
                ret.allMethods.Add(methodName, methodData);
            }

            foreach(var (memberName, memberRef) in typeData.members)
            {
                ret.allMembers.Add(memberName, memberRef);
            }
        }

        lock(_locker)
        {
            if (!_moduleDatas.TryAdd(moduleName, ret))
                ret = _moduleDatas[moduleName];
        }
        
        return ret;
    }

    static void ClearAll()
    {
        lock(_locker)
        {
            foreach (var kv in _moduleDatas)
            {
                kv.Value.moduleDef.Dispose();
            }
            _moduleDatas.Clear();
        }
    }

    static void CreateCtx()
    {
        {
            var baseResolver = new dnlib.DotNet.AssemblyResolver();
            ctx = new ModuleContext(baseResolver, null);
            baseResolver.DefaultModuleContext = ctx;

            foreach (var ass in GlobalConfig.Instance.searchPaths)
                baseResolver.PostSearchPaths.Add(ass);
            baseResolver.PostSearchPaths.Add(GlobalConfig.Instance.builtinAssembliesDir);
        }
    }

    static void GenTypeInfos(TypeDef typeDefinition, TypeData parent, Dictionary<string, TypeData> types)
    {
        /*
         * 此方法会被多线程调用，因此不可以使用类变量
         */
        TypeData typeData = new TypeData();
        typeData.definition = typeDefinition;
        typeData.parent = parent;

        string sig = typeDefinition.ToString();
        typeData.isLambdaStaticType = Utils.IsLambdaStaticType(sig);
        types.Add(sig, typeData);

        if (typeData.isLambdaStaticType)
            parent.childLambdaStaticType = typeData;

        foreach (var method in typeDefinition.Methods)
        {
            if (method.IsAbstract || !method.HasBody)
                continue;

            // property 的 getter, setter 也属于 method，且 IsGetter, IsSetter 字段会设置为 true, 因此无需单独遍历 properties
            var data = new MethodData(typeData, method, Utils.IsLambdaMethod(method));
            typeData.methods.Add(data.fullName, data);
            typeData.members.Add(data.fullName, method);
        }

        foreach (var field in typeDefinition.Fields)
        {
            typeData.members.Add(field.ToString(), field); // 带 event 标识的字段也是字段，il 层面没有 event 这个东西
        }

        foreach(var prop in typeDefinition.Properties)
        {
            typeData.members.Add(prop.ToString(), prop);
        }

        foreach (var nest in typeDefinition.NestedTypes)
        {
            GenTypeInfos(nest, typeData, types);
        }
    }
}

public class ModuleDefData
{
    public string name;
    public ModuleDef moduleDef;

    public AssemblyRefUser assRef;
    public ModuleRefUser moduleRef;

    public Dictionary<string, TypeData> types;
    public Dictionary<string, MethodData> allMethods; // 所有方法，用于快速访问
    public Dictionary<string, IMemberRef> allMembers; // 所有的方法，字段，事件，属性，用于快速访问

    public void Unload()
    {
        moduleDef?.Dispose();
        moduleDef = null;
    }
}

public class TypeData
{
    public TypeSig typeSig { get
        {
            if (_typeSig == null)
                _typeSig = definition.ToTypeSig();
            return _typeSig;
        }
    }
    private TypeSig _typeSig;

    // 不会记录 GenericInstanceType, FixMethod 时遇到会动态创建并替换
    public TypeDef definition;

    public bool isLambdaStaticType; // 名字为 `<>` 的类型
    public TypeData parent;
    public TypeData childLambdaStaticType; // TypeName/<>c

    public Dictionary<string, MethodData> methods = new Dictionary<string, MethodData>();
    public Dictionary<string, IMemberRef> members = new Dictionary<string, IMemberRef>(); // 方法，字段，事件，属性
    public HashSet<PdbDocument> pdbDocuments = new HashSet<PdbDocument>();

    public override string ToString() => definition.ToString();
}

public class MethodData
{
    // 不会记录 GenericInstanceMethod, FixMethod 时遇到会动态创建并替换
    public string fullName;
    public TypeData typeData;
    public MethodDef definition;
    public bool isLambda;
    public PdbDocument document;

    public MethodData(TypeData typeData, MethodDef definition, bool isLambda)
    {
        fullName = definition.ToString();
        this.typeData = typeData; this.definition = definition; this.isLambda = isLambda;
        this.document = Utils.GetDocOfMethod(definition);
        if(document != null)
            typeData.pdbDocuments.Add(document);
    }

    public JSONNode ToJsonNode()
    {
        JSONObject ret = new JSONObject();
        ret["name"] = definition.Name.String; // TODO 对于泛型需要输出为 FuncA`1 这种格式吗？
        ret["type"] = Utils.GetRuntimeTypeName(typeData.definition.ToTypeSig());
        ret["assembly"] = typeData.definition.Module.Name.ToString();
        ret["isConstructor"] = definition.IsConstructor;
        ret["isGeneric"] = Utils.IsGeneric(definition);
        ret["isPublic"] = definition.IsPublic;
        ret["isStatic"] = definition.IsStatic;
        ret["isLambda"] = isLambda;
        ret["isGetterOrSetter"] = definition.IsGetter | definition.IsSetter;
        ret["document"] = document?.Url.Substring(Environment.CurrentDirectory.Length + 1).Replace('\\', '/');

        if (!definition.IsConstructor)
            ret["returnType"] = Utils.GetRuntimeTypeName(definition.ReturnType);

        JSONArray paraArr = new JSONArray();
        ret.Add("paramTypes", paraArr);
        var paras = definition.Parameters;

        int skipIdx = definition.HasThis ? 1 : 0;
        for(int i = 0, imax = paras.Count - skipIdx; i < imax; i++)
        {
            paraArr[i] = Utils.GetRuntimeTypeName(paras[i + skipIdx].Type);
        }

        return ret;
    }

    public override string ToString() => definition.ToString();
}