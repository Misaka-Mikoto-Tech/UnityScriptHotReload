using dnlib.DotNet;
using System.Collections.Generic;
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
    public List<TypeSig> typeGenArgs;
    public List<TypeSig> methodGenArgs;

    public override string ToString() => method.ToString();
}

/// <summary>
/// 扫描出来的类型实例数据，用来对同时具有类型泛型参数和方法类型参数的方法实例进行 CrossGen
/// </summary>
public class ScannedTypeSpecs
{
    public TypeDef genericDef;
    public Dictionary<string, TypeSpec> typeSpecs = new Dictionary<string, TypeSpec>();
}

/// <summary>
/// 泛型实例参数
/// </summary>
public class GenericInstArgs
{
    public List<TypeSig> typeGenArgs;
    public List<TypeSig> methodGenArgs;
    public IMethod instMethodInBase;

    public MethodDef wrapperMethodDef; // wrapper 函数生成后填充
    public IMethod instMethodInPatch;  // wrapper 函数生成后填充

    public override string ToString() => instMethodInBase.ToString();
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
    /// <summary>
    /// patch dll 内发生改变文件定义的非泛型方法。
    /// </summary>
    /// <remarks>TODO 此处不合适，只是顺便扫描，考虑放到更合适的位置</remarks>
    public List<MethodData> nonGenericMethodInPatch { get; private set; } = new List<MethodData>();

    private Dictionary<string, ScannedTypeSpecs> _typeSpecDic = new Dictionary<string, ScannedTypeSpecs>();
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
        // Document.Url 是当前平台默认分隔符的全路径，此处提前转换一下
        var fileChanged = new List<string>();
        char spliter = Path.DirectorySeparatorChar;
        foreach (var f in GlobalConfig.Instance.filesToCompile[_assemblyDataForPatch.name])
        {
            var fullPath = $"{Environment.CurrentDirectory}{spliter}{f.Replace('/', spliter)}";
            fileChanged.Add(fullPath);
        }

        _gernericMethodFilter = new Dictionary<string, MethodDef>();
        nonGenericMethodInPatch.Clear();
        foreach (var (_, methodData) in _assemblyDataForPatch.patchDllData.allMethods)
        {
            // 只对发生改变的源码文件内定义的方法生成wrapper
            // 没有 document 的也不处理（编译器自动生成的默认方法）
            if (methodData.document == null || !fileChanged.Contains(methodData.document.Url))
                continue;

            var method = methodData.definition;
            if (method.HasGenericParameters || method.DeclaringType.HasGenericParameters) // TODO Nested Type?
                _gernericMethodFilter.Add(method.FullName, method.ResolveMethodDef());
            else
                nonGenericMethodInPatch.Add(methodData);
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
        var assembly = _baseModuleDef.Assembly;
        CorLibTypeSig objSig = assembly.ManifestModule.CorLibTypes.Object;
        List<TypeSig> argList = new List<TypeSig>();

        uint count = _baseModuleDef.Metadata.TablesStream.TypeSpecTable.Rows;
        for (uint i = 1; i <= count; i++) // rowID 从1开始计数
        {
            TypeSpec ts = _baseModuleDef.ResolveTypeSpec(i);
            if (ts.ContainsGenericParameter)
                continue;

            TypeDef genericTypeDef = ts.ResolveTypeDef();
            if (genericTypeDef == null || genericTypeDef.DefinitionAssembly != assembly)
                continue;

            string genericName = genericTypeDef.ToString();
            if(!_typeSpecDic.TryGetValue(genericName, out var typeSpecsInfo))
            {
                typeSpecsInfo = new ScannedTypeSpecs();
                typeSpecsInfo.genericDef = genericTypeDef;
                _typeSpecDic.Add(genericName, typeSpecsInfo);
            }

            // 前天合并泛型参数类型，减少CrossGen的数量（此处不合并最后合并阶段也会被合并，但是中间运算量会变大）
            var sig = ts.TypeSig.RemovePinnedAndModifiers();
            if (sig is SZArraySig) // 数组类型的不记录，因为不会存在数组类型的 MethodSpec 这种情况
                continue;
            var instSig = sig as GenericInstSig;

            argList.Clear();
            argList.AddRange(instSig.GenericArguments);
            bool needRecreateTs = false;
            for(int j = 0, jmax = argList.Count; j < jmax; j++)
            {
                if (argList[j].IsByRef)
                {
                    argList[j] = objSig;
                    needRecreateTs = true;
                }
            }

            if (needRecreateTs)
            {
                var genericSig = genericTypeDef.ToTypeSig() as ClassOrValueTypeSig;
                var newSig = new GenericInstSig(genericSig, argList);
                TypeSpec newTs = new TypeSpecUser(newSig);

                typeSpecsInfo.typeSpecs.TryAdd(newTs.ToString(), newTs);
            }
            else
                typeSpecsInfo.typeSpecs.TryAdd(ts.ToString(), ts);
        }
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
                instData.typeGenArgs =new List<TypeSig>(sig.GenericArguments);
                instData.methodGenArgs = new List<TypeSig>();
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

            /*
             * 有可能是 TypeDef 或者 TypeSpec
             * NS_Test_Generic.TestClsG`1<T> 这种也是 TypeSpec
             */
            var declType = ms.DeclaringType;

            var methodSig = ms.GenericInstMethodSig;
            if (methodSig.ContainsGenericParameter)
                continue;

            // 获取实例类型的泛型原始类型
            var resolvedMethod = ms.Method.ResolveMethodDef();
            var fullName = resolvedMethod.FullName;
            if (!_gernericMethodFilter.TryGetValue(fullName, out var patchMethodDef))
                continue;

            Action<MethodSpec, ITypeDefOrRef> addMethodSpec = (MethodSpec ms_, ITypeDefOrRef finalType) =>
            {
                var instData = new ScannedMethodInfo();
                instData.method = ms_;
                instData.genericMethodInBase = resolvedMethod;
                instData.genericMethodInPatch = patchMethodDef;

                if (finalType.ToTypeSig() is GenericInstSig genericSig)
                    instData.typeGenArgs = new List<TypeSig>(genericSig.GenericArguments);
                else
                    instData.typeGenArgs = new List<TypeSig>();

                instData.methodGenArgs = new List<TypeSig>(methodSig.GenericArguments); // 只需要获取 Mvar, Var不关注，因此可以重用这个变量

                _scannedMethodInfos.Add(instData);
            };

            /*
             * 如果 MethodSpec 属于一个 TypeSpc，dll 的 MetaData 内是没有存储其所属泛型类型实例的，
             * 因此只能暴力CrossGen, 代价是生成的MethodSpec数量比较多
             */
            if (declType.ContainsGenericParameter)
            {
                var genericType = declType.ResolveTypeDef();
                if (!_typeSpecDic.TryGetValue(genericType.ToString(), out var instData))
                    return;

                // 获取泛型方法签名
                var genericMethodSig = ms.Method.MethodSig; // T <!!0>(T,U)
                foreach (var (_, typeSpec) in instData.typeSpecs)
                {
                    /*
                     * 创建填充类型泛型参数的泛型方法引用
                     * System.Single NS_Test.TestClsG`1<System.Single>::ShowGA<!!0>(System.Single,U)
                     */
                    var baseGenericMethodRef = new MemberRefUser(_assemblyDataForPatch.baseDllData.moduleDef, ms.Name, genericMethodSig, typeSpec);
                    var newMethodSpec = new MethodSpecUser(baseGenericMethodRef, ms.GenericInstMethodSig);
                    addMethodSpec(newMethodSpec, typeSpec);
                }
            }
            else
            {
                addMethodSpec(ms, declType);
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
                    genInstArgs.typeGenArgs = new List<TypeSig>(typeSigs);
                    genInstArgs.methodGenArgs = new List<TypeSig>(methodSigs);
                    genInstArgs.instMethodInBase = instScanned.method;
                    genMethodData.genericInsts.Add(genInstArgs);
                }
            }
        }

        return ret.Values.ToList();
    }
}
