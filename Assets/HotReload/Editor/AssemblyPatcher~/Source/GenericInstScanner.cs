using dnlib.DotNet;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace AssemblyPatcher;

/// <summary>
/// 记录从MetaData扫描到的需要hook的泛型方法定义及实例信息
/// </summary>
public class MethodInstData
{
    /// <summary>
    /// MemberRef or MethodSpec
    /// </summary>
    public IMethod method;
    /// <summary>
    /// 此方法对应的具有泛型参数的方法定义（方法存在泛型参数或者类型存在泛型参数或者两者均有）
    /// 其 rid 与 resolved method 相同
    /// </summary>
    public MethodDef genericMethodInBase;
    public MethodDef genericMethodInPatch;
    public List<TypeSig> typeGenArgs = new List<TypeSig>();
    public List<TypeSig> methodGenArgs = new List<TypeSig>();

    public override string ToString() => method.ToString();
}

public class GenericInstData
{
    public MethodDef genericMethodInBase;
    public MethodDef genericMethodInPatch;
    /// <summary>
    /// 所有实现此泛型定义的所有实例（引用类型已被合并为 System.Object, 其它类型改为通过 Import 替换为Patch dll 内的 TypeSig）
    /// </summary>
    public List<List<TypeSig>> typeGenArgs = new List<List<TypeSig>>();
    public List<List<TypeSig>> methodGenArgs = new List<List<TypeSig>>();
    /// <summary>
    /// 原始泛型实例数据，一般仅用于调试
    /// </summary>
    public List<MethodInstData> instMethods = new List<MethodInstData>();

    public override string ToString() => genericMethodInBase.ToString();
}

/// <summary>
/// 泛型实例扫描器
/// </summary>
public class GenericInstScanner
{
    private AssemblyDataForPatch _assemblyDataForPatch;
    private ModuleDefMD _baseModuleDef;
    private Dictionary<string, MethodDef> _gernericMethodFilter; // 需要过滤的方法，value: 泛型定义
    Importer _importer;

    private List<MethodInstData> _methodInstDatas = new List<MethodInstData>();

    /// <summary>
    /// 泛型实例扫描器
    /// </summary>
    /// <param name="baseModuleDef">原始dll的对象</param>
    /// <param name="gernericMethodFilter">patch dll中定义的包含泛型参数的所有方法定义全名</param>
    public GenericInstScanner(AssemblyDataForPatch assemblyDataForPatch, Importer importer)
    {
        _assemblyDataForPatch = assemblyDataForPatch;
        _baseModuleDef = assemblyDataForPatch.baseDllData.moduleDef as ModuleDefMD;
        _importer = importer;
    }

    /// <summary>
    /// 扫描出 base dll 内泛型方法定义及其所有的不包含泛型参数的实例
    /// </summary>
    /// <returns></returns>
    public List<GenericInstData> Scan()
    {
        _gernericMethodFilter = new Dictionary<string, MethodDef>();
        foreach (var (_, methodData) in _assemblyDataForPatch.patchDllData.allMethods)
        {
            var method = methodData.definition;
            if (method.HasGenericParameters || method.DeclaringType.HasGenericParameters) // TODO Nested Type?
                _gernericMethodFilter.Add(method.FullName, method.ResolveMethodDef());
        }

        _methodInstDatas.Clear();
        ScanMethodDefFromMetaData();
        ScanMethodInstFromMetaData();

        // 整理泛型实例数据
        var ret = MergeAndImportGenericInstDatas();
        return ret;
    }

    /// <summary>
    /// 扫描泛型类型内的非泛型方法
    /// </summary>
    void ScanMethodDefFromMetaData()
    {
        var assembly = _baseModuleDef.Assembly;

        uint count = _baseModuleDef.Metadata.TablesStream.MemberRefTable.Rows;
        for (uint i = 1; i <= count; i++) // rowID 从1开始计数
        {
            var mr = _baseModuleDef.ResolveMemberRef(i);
            if (!mr.IsMethodRef)
                continue;

            /*
             * MemberRef 也有可能带有泛型参数
             * eg. {NS_Test.TestDll_2 NS_Test.TestClsG`1/TestClsGInner`1<NS_Test.TestCls,NS_Test.TestDll_2>::ShowGInner<!!0>(NS_Test.TestCls,NS_Test.TestDll_2,!!0)}
             */
            if (mr.MethodSig.ContainsGenericParameter)
                continue;

            var declType = mr.DeclaringType;
            /*
             * {NS_Test.TestClsG`1/TestClsGInner`1<!0,!1>}
             * 这种定义也是 TypeSpecMD, 但是包含泛型参数，因此条件不符合
             */
            if (!declType.IsTypeSpec || declType.ContainsGenericParameter || declType.DefinitionAssembly != assembly)
                continue;

            var typeSpec = declType as TypeSpecMD;

            /*
             * eg. {System.Boolean NS_Test.TestClsG`1<UnityEngine.Vector3>::FuncA(System.Int32)}
             *      to => 
             */
            var resoledMethod = mr.ResolveMethod();
            if(_gernericMethodFilter.TryGetValue(resoledMethod.FullName, out var patchMethodDef))
            {
                var sig = typeSpec.TypeSig as GenericInstSig;

                var instData = new MethodInstData();
                instData.method = mr;
                instData.genericMethodInBase = resoledMethod;
                instData.genericMethodInPatch = patchMethodDef;
                instData.typeGenArgs.AddRange(sig.GenericArguments);
                _methodInstDatas.Add(instData);
            }
        }
    }

    /// <summary>
    /// 扫描dll内定义的泛型方法的实例
    /// </summary>
    void ScanMethodInstFromMetaData()
    {
        var assembly = _baseModuleDef.Assembly;
        uint count = _baseModuleDef.Metadata.TablesStream.MethodSpecTable.Rows;
        for (uint i = 1; i <= count; i++) // rowID 从1开始计数
        {
            MethodSpecMD ms = _baseModuleDef.ResolveMethodSpec(i) as MethodSpecMD;

            var declType = ms.DeclaringType;
            /*
             * V NS_Test.TestClsG`1/TestClsGInner`1<T,V>::ShowGInner<System.Int64>(T,V,System.Int64)
             * 这种带泛型参数的类型是不允许的
             */
            if (declType.ContainsGenericParameter || declType.DefinitionAssembly != assembly)
                continue;

            var methodSig = ms.GenericInstMethodSig;
            if (methodSig.ContainsGenericParameter)
                continue;

            // 获取实例类型的泛型原始类型
            var resolvedMethod = ms.Method.ResolveMethodDef();
            var fullName = resolvedMethod.FullName;
            if (_gernericMethodFilter.TryGetValue(fullName, out var patchMethodDef))
            {
                var instData = new MethodInstData();
                instData.method = ms;
                instData.genericMethodInBase = resolvedMethod;
                instData.genericMethodInPatch = patchMethodDef;

                if(declType.ToTypeSig() is GenericInstSig genericSig)
                    instData.typeGenArgs.AddRange(genericSig.GenericArguments);

                instData.methodGenArgs.AddRange(methodSig.GenericArguments);

                _methodInstDatas.Add(instData);
            }
        }
    }

    /// <summary>
    /// 合并并导入泛型实例数据
    /// </summary>
    /// <returns></returns>
    List<GenericInstData> MergeAndImportGenericInstDatas()
    {
        var ret = new Dictionary<MethodDef, GenericInstData>();
        // 合并 GenericMethod
        foreach (var instData in _methodInstDatas)
        {
            if (!ret.TryGetValue(instData.genericMethodInBase, out var genericInstData))
            {
                genericInstData = new GenericInstData();
                genericInstData.genericMethodInBase = instData.genericMethodInBase;
                genericInstData.genericMethodInPatch = instData.genericMethodInPatch;
                ret.Add(instData.genericMethodInBase, genericInstData);
            }
            genericInstData.instMethods.Add(instData);
        }

        var uniqueArgsDic = new Dictionary<string, (List<TypeSig> typeSigs, List<TypeSig> methodSigs)>();
        StringBuilder sb = new StringBuilder();
        var objectTypeSig = _assemblyDataForPatch.patchDllData.moduleDef.CorLibTypes.Object;

        // 合并System.Object的类型
        foreach (var genInstData in ret.Values)
        {
            uniqueArgsDic.Clear();
            List<TypeSig> typeSigs = new List<TypeSig>();
            List<TypeSig> methodSigs = new List<TypeSig>();
            foreach (var instData in genInstData.instMethods)
            {
                sb.Clear();
                typeSigs.Clear();
                methodSigs.Clear();
                foreach(var tSig in instData.typeGenArgs)
                {
                    TypeSig newSig = tSig;
                    if (!tSig.IsValueType)
                        newSig = objectTypeSig;
                    else
                        newSig = _importer.Import(newSig);

                    sb.Append(newSig.ToString()).Append(",\t");
                    typeSigs.Add(newSig);
                }
                foreach (var tSig in instData.methodGenArgs)
                {
                    TypeSig newSig = tSig;
                    if (!tSig.IsValueType)
                        newSig = objectTypeSig;
                    else
                        newSig = _importer.Import(newSig);

                    sb.Append(newSig.ToString()).Append(",\t");
                    methodSigs.Add(newSig);
                }

                uniqueArgsDic.TryAdd(sb.ToString(), (typeSigs, methodSigs));
            }
            genInstData.typeGenArgs.AddRange(from sigs in uniqueArgsDic.Values select sigs.typeSigs);
            genInstData.methodGenArgs.AddRange(from sigs in uniqueArgsDic.Values select sigs.methodSigs);
        }

        return ret.Values.ToList();
    }
}
