using dnlib.DotNet;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace AssemblyPatcher;

/*
 * 添加wrapper 以跳过从vtable获取类型, mini-exceptions.c#L1379
 * 		* // actual_method might already be set by mono_arch_unwind_frame () *
		if (!frame.actual_method) {
			if ((unwind_options & MONO_UNWIND_LOOKUP_ACTUAL_METHOD) && frame.ji)
				frame.actual_method = get_method_from_stack_frame(frame.ji, get_generic_info_from_stack_frame (frame.ji, &ctx));
			else
				frame.actual_method = frame.method;
		}
 */

/// <summary>
/// 记录从MetaData扫描到的需要hook的泛型方法定义及实例信息
/// TODO metadata 内的 MethodSpec 信息不全，改为扫描IL获取
/// </summary>
public class ScannedMethodInfo
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

/// <summary>
/// 泛型实例参数
/// </summary>
public class GenericInstArgs
{
    public List<TypeSig> typeGenArgs = new List<TypeSig>();
    public List<TypeSig> methodGenArgs = new List<TypeSig>();
    public IMethod instMethodInBase;

    public MethodDef wrapperMethodDef; // wrapper 函数生成后填充
    public IMethod instMethodInPatch;  // wrapper 函数生成后填充
}

/// <summary>
/// 泛型方法的相关数据（记录的所有实例等数据）
/// </summary>
public class GenericMethodData
{
    public MethodDef genericMethodInBase;
    public MethodDef genericMethodInPatch;
    /// <summary>
    /// 所有实现此泛型定义的所有实例（引用类型已被合并为 System.Object, 其它类型改为通过 Import 替换为Patch dll 内的 TypeSig）
    /// </summary>
    public List<GenericInstArgs> genericInsts = new List<GenericInstArgs>();

    /// <summary>
    /// 原始泛型实例数据，一般仅用于调试
    /// </summary>
    public List<ScannedMethodInfo> instMethodsScanned = new List<ScannedMethodInfo>();

    public override string ToString() => genericMethodInBase.ToString();
}

/// <summary>
/// 泛型实例扫描器
/// </summary>
public class GenericInstScanner
{
    public List<GenericMethodData> genericMethodDatas { get; private set; } = new List<GenericMethodData>();

    private AssemblyDataForPatch    _assemblyDataForPatch;
    private ModuleDefMD             _baseModuleDef;
    private Dictionary<string, MethodDef> _gernericMethodFilter; // 需要过滤的方法，value: Patch dll内的包含泛型的方法定义
    Importer    _importer;

    private List<ScannedMethodInfo> _scannedMethodInfos = new List<ScannedMethodInfo>();

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
    public void Scan()
    {
        _gernericMethodFilter = new Dictionary<string, MethodDef>();
        foreach (var (_, methodData) in _assemblyDataForPatch.patchDllData.allMethods)
        {
            var method = methodData.definition;
            if (method.HasGenericParameters || method.DeclaringType.HasGenericParameters) // TODO Nested Type?
                _gernericMethodFilter.Add(method.FullName, method.ResolveMethodDef());
        }

        _scannedMethodInfos.Clear();
        ScanTypeSpecFromMetaData();
        ScanMethodDefFromMetaData();
        ScanMethodInstFromMetaData();

        // 整理泛型实例数据
        genericMethodDatas = MergeAndImportGenericInstDatas();
    }

    /// <summary>
    /// 扫描泛型类型实例
    /// （某些类似 `ClassA<T>.Func1()`，`ClassA<T>.Func2<int>()` 签名的 MethodSpec 和 MemberRef 被间接调用时无法确定类型, 只能 Cross Generate）
    /// </summary>
    void ScanTypeSpecFromMetaData()
    {
        // TODO 是否有跟踪数据流的方式确定所属真实类型(但这样需要遍历原始dll内所有的il，所以还是暴力Cross 原始dll内的实例meta更简单？)

    }

    /// <summary>
    /// 扫描泛型实例类型内的非泛型方法
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
             * ClassA<T>.Func1() 这种也是MemberRef
             */
            if (mr.MethodSig.ContainsGenericParameter || mr.MethodSig.Generic)
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

                var instData = new ScannedMethodInfo();
                instData.method = mr;
                instData.genericMethodInBase = resoledMethod;
                instData.genericMethodInPatch = patchMethodDef;
                instData.typeGenArgs.AddRange(sig.GenericArguments);
                _scannedMethodInfos.Add(instData);
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
             * 这种带泛型参数的类型是不允许的（但 dotnet 只记录了一级，此处没有记录调用者的泛型类型, 考虑将此类型合并进所有的其所属泛型类型内）
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
                var instData = new ScannedMethodInfo();
                instData.method = ms;
                instData.genericMethodInBase = resolvedMethod;
                instData.genericMethodInPatch = patchMethodDef;

                if(declType.ToTypeSig() is GenericInstSig genericSig)
                    instData.typeGenArgs.AddRange(genericSig.GenericArguments);

                instData.methodGenArgs.AddRange(methodSig.GenericArguments);

                _scannedMethodInfos.Add(instData);
            }
        }
    }

    /// <summary>
    /// 合并并导入泛型实例数据
    /// </summary>
    /// <returns></returns>
    List<GenericMethodData> MergeAndImportGenericInstDatas()
    {
        var ret = new Dictionary<MethodDef, GenericMethodData>(); // key: genericMethodInBase
        // 合并 GenericMethod
        foreach (var methodInfo in _scannedMethodInfos)
        {
            if (!ret.TryGetValue(methodInfo.genericMethodInBase, out var genericMethodData))
            {
                genericMethodData = new GenericMethodData();
                genericMethodData.genericMethodInBase = methodInfo.genericMethodInBase;
                genericMethodData.genericMethodInPatch = methodInfo.genericMethodInPatch;
                ret.Add(methodInfo.genericMethodInBase, genericMethodData);
            }
            genericMethodData.instMethodsScanned.Add(methodInfo);
        }

        var uniqueArgsHash = new HashSet<string>();
        StringBuilder sb = new StringBuilder();
        var objectTypeSig = _assemblyDataForPatch.patchDllData.moduleDef.CorLibTypes.Object;

        // 合并System.Object的类型
        foreach (var genMethodData in ret.Values)
        {
            uniqueArgsHash.Clear();
            List<TypeSig> typeSigs = new List<TypeSig>();
            List<TypeSig> methodSigs = new List<TypeSig>();
            foreach (var instScanned in genMethodData.instMethodsScanned)
            {
                sb.Clear();
                typeSigs.Clear();
                methodSigs.Clear();
                foreach(var tSig in instScanned.typeGenArgs)
                {
                    TypeSig newSig = tSig;
                    if (!tSig.IsValueType)
                        newSig = objectTypeSig;
                    else
                        newSig = _importer.Import(newSig);

                    sb.Append(newSig.ToString()).Append(",\t");
                    typeSigs.Add(newSig);
                }
                foreach (var tSig in instScanned.methodGenArgs)
                {
                    TypeSig newSig = tSig;
                    if (!tSig.IsValueType)
                        newSig = objectTypeSig;
                    else
                        newSig = _importer.Import(newSig);

                    sb.Append(newSig.ToString()).Append(",\t");
                    methodSigs.Add(newSig);
                }

                if(!uniqueArgsHash.Contains(sb.ToString()))
                {
                    uniqueArgsHash.Add(sb.ToString());
                    var genInstArgs = new GenericInstArgs();
                    genInstArgs.typeGenArgs = typeSigs;
                    genInstArgs.methodGenArgs = methodSigs;
                    genInstArgs.instMethodInBase = instScanned.method;
                    genMethodData.genericInsts.Add(genInstArgs);
                }
            }
        }

        return ret.Values.ToList();
    }
}
