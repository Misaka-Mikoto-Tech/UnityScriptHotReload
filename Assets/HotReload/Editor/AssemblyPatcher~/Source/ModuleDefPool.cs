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
        // 提前把相关dll都载入，方便查找对应类型(需要在Patch Dll生成完毕之后再调用)
        Stopwatch sw = new Stopwatch();
        sw.Start();
        foreach (var kv in GlobalConfig.Instance.assemblyPathes)
        {
            try
            {
                var ass = Assembly.LoadFrom(kv.Value);
                _allAssemblies.Add(kv.Key, ass);
            }
            catch (Exception ex)
            {
                Debug.LogError($"load dll fail:{kv.Value}\r\n:{ex.Message}\r\n{ex.StackTrace}");
            }
        }
        sw.Stop();
        Debug.LogDebug($"载入相关dll耗时 {sw.ElapsedMilliseconds} ms");

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
        ret.assembly = _allAssemblies[moduleName];
        ret.moduleDef = ModuleDefMD.Load(fullPath, ctx);

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
            // 静态构造函数不允许被hook，否则静态构造函数会被自动再次执行导致数据错误
            if (method.IsAbstract || !method.HasBody || method.Name.Contains(".cctor"))
                continue;

            // property getter, setter 也包含在内，且 IsGetter, IsSetter 字段会设置为 true, 因此无需单独遍历 properties
            var data = new MethodData(typeData, method, null, Utils.IsLambdaMethod(method));
            string fullName = method.ToString();
            typeData.methods.Add(fullName, data);
            typeData.members.Add(fullName, method);
        }

        foreach (var field in typeDefinition.Fields)
        {
            typeData.members.Add(field.ToString(), field); // Events 也包含在 Fields 内 
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

    /// <summary>
    /// 由于并不是每个 ModuleData 都需要这个字段，因此按需填充
    /// </summary>
    public static void FillReflectMethodField(ModuleDefData moduleDefData)
    {
        Assembly ass = (from ass_ in _allAssemblies.Values where ass_.ManifestModule.Name == moduleDefData.moduleDef.Name select ass_).FirstOrDefault();
        Debug.Assert(ass != null);

        var dicTypes = new Dictionary<TypeDef, Type>();
        foreach (var (_, typeData) in moduleDefData.types)
        {
            foreach(var (_, methodData) in typeData.methods)
            {
                if (methodData.reflectMethod != null)
                    continue;

                var definition = methodData.definition;
                if (!dicTypes.TryGetValue(definition.DeclaringType, out var t))
                {
                    t = ass.GetType(definition.DeclaringType.FullName.Replace('/', '+'));
                    Debug.Assert(t != null);
                    dicTypes.Add(definition.DeclaringType, t);
                }
                methodData.reflectMethod = Utils.GetReflectMethodSlow(t, definition);
                if (methodData.reflectMethod == null)
                {
                    Debug.LogError($"can not find MethodInfo of [{methodData.definition.FullName}]");
                }
            }
        }
    }
}

public class ModuleDefData
{
    public string name;
    public Assembly assembly;
    public ModuleDef moduleDef;

    public AssemblyRefUser assRef;
    public ModuleRefUser moduleRef;

    public Dictionary<string, TypeData> types;
    public Dictionary<string, MethodData> allMethods; // 所有方法，用于快速访问
    public Dictionary<string, IMemberRef> allMembers; // 所有的方法，字段，事件，属性，用于快速访问
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
}

public class MethodData
{
    // 不会记录 GenericInstanceMethod, FixMethod 时遇到会动态创建并替换
    public TypeData typeData;
    public MethodDef definition;
    public MethodBase reflectMethod;
    public bool isLambda;
    public bool ilChanged;
    public PdbDocument document;

    public MethodData(TypeData typeData, MethodDef definition, MethodInfo methodInfo, bool isLambda)
    {
        this.typeData = typeData; this.definition = definition; this.reflectMethod = methodInfo; this.isLambda = isLambda;
        this.document = Utils.GetDocOfMethod(definition);
    }

    public JSONNode ToJsonNode()
    {
        string moduleName = typeData.definition.Module.Name;

        JSONObject ret = new JSONObject();
        ret["name"] = reflectMethod.Name;
        ret["type"] = Utils.GetRuntimeTypeName(reflectMethod.DeclaringType, reflectMethod.ContainsGenericParameters);
        ret["assembly"] = typeData.definition.Module.Name.ToString();
        ret["isConstructor"] = reflectMethod.IsConstructor;
        ret["isGeneric"] = reflectMethod.ContainsGenericParameters;
        ret["isPublic"] = reflectMethod.IsPublic;
        ret["isStatic"] = reflectMethod.IsStatic;
        ret["isLambda"] = isLambda;
        ret["ilChanged"] = ilChanged;
        ret["document"] = document.Url.Substring(Environment.CurrentDirectory.Length + 1);

        if (!reflectMethod.IsConstructor)
            ret["returnType"] = (reflectMethod as MethodInfo).ReturnType.ToString();

        JSONArray paraArr = new JSONArray();
        ret.Add("paramTypes", paraArr);
        var paras = reflectMethod.GetParameters();
        for (int i = 0, imax = paras.Length; i < imax; i++)
        {
            paraArr[i] = Utils.GetRuntimeTypeName(paras[i].ParameterType, reflectMethod.ContainsGenericParameters);
        }

        return ret;
    }

    public override string ToString() => definition.ToString();
}