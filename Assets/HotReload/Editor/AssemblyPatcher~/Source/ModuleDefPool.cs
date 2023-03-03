using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using SimpleJSON;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace AssemblyPatcher;

/// <summary>
/// ModuleDef 池，避免多次Load同一个Module
/// </summary>
public static class ModuleDefPool
{
    static ModuleContext _ctx;
    static Dictionary<string, Assembly> _allAssemblies = new Dictionary<string, Assembly>();
    static Dictionary<string, ModuleDefData> _moduleDatas = new Dictionary<string, ModuleDefData>();

    static ModuleDefPool()
    {
        // 提前把相关dll都载入，方便查找对应类型
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
        if(!_moduleDatas.TryGetValue(moduleName, out var defData))
        {
            string fullPath = GlobalConfig.Instance.assemblyPathes[moduleName];

            defData = new ModuleDefData();
            defData.moduleDef = ModuleDefMD.Load(fullPath, _ctx);
            defData.assembly = _allAssemblies[moduleName];

            var types = defData.moduleDef.Types;
            foreach (var t in types)
                GenTypeInfos(t, null, defData.types);

            _moduleDatas.Add(moduleName, defData);
        }

        return defData;
    }

    static void ClearAll()
    {
        foreach(var kv in _moduleDatas)
        {
            kv.Value.moduleDef.Dispose();
        }
        _moduleDatas.Clear();
    }

    static void CreateCtx()
    {
        {
            var baseResolver = new dnlib.DotNet.AssemblyResolver();
            _ctx = new ModuleContext(baseResolver, null);
            baseResolver.DefaultModuleContext = _ctx;

            foreach (var ass in GlobalConfig.Instance.searchPaths)
                baseResolver.PostSearchPaths.Add(ass);
            baseResolver.PostSearchPaths.Add(GlobalConfig.Instance.builtinAssembliesDir);
        }
    }

    static void GenTypeInfos(TypeDef typeDefinition, TypeData parent, Dictionary<string, TypeData> types)
    {
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
            typeData.methods.Add(method.ToString(), data);
        }

        foreach (var field in typeDefinition.Fields)
        {
            typeData.fields.Add(field.ToString(), field);
        }

        foreach (var nest in typeDefinition.NestedTypes)
        {
            GenTypeInfos(nest, typeData, types);
        }
    }
}

public class ModuleDefData
{
    public ModuleDef moduleDef;
    public Assembly assembly;
    public Dictionary<string, TypeData> types;
}

public class TypeData
{
    // 不会记录 GenericInstanceType, FixMethod 时遇到会动态创建并替换
    public TypeDef definition;

    public bool isLambdaStaticType; // 名字为 `<>` 的类型
    public TypeData parent;
    public TypeData childLambdaStaticType; // TypeName/<>c

    public Dictionary<string, MethodData> methods = new Dictionary<string, MethodData>();
    public Dictionary<string, FieldDef> fields = new Dictionary<string, FieldDef>();
}

public class MethodData
{
    // 不会记录 GenericInstanceMethod, FixMethod 时遇到会动态创建并替换
    public TypeData typeData;
    public MethodDef definition;
    public MethodBase methodInfo;
    public bool isLambda;
    public bool ilChanged;
    public PdbDocument document;

    public MethodData(TypeData typeData, MethodDef definition, MethodInfo methodInfo, bool isLambda)
    {
        this.typeData = typeData; this.definition = definition; this.methodInfo = methodInfo; this.isLambda = isLambda;
        this.document = Utils.GetDocOfMethod(definition);
    }

    public JSONNode ToJsonNode()
    {
        string moduleName = typeData.definition.Module.Name;

        JSONObject ret = new JSONObject();
        ret["name"] = methodInfo.Name;
        ret["type"] = Utils.GetRuntimeTypeName(methodInfo.DeclaringType, methodInfo.ContainsGenericParameters);
        ret["assembly"] = typeData.definition.Module.Name.ToString();
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
        for (int i = 0, imax = paras.Length; i < imax; i++)
        {
            paraArr[i] = Utils.GetRuntimeTypeName(paras[i].ParameterType, methodInfo.ContainsGenericParameters);
        }

        return ret;
    }

    public override string ToString() => definition.ToString();
}