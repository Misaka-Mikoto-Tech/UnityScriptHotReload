/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */

using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace AssemblyPatcher;

/// <summary>
/// 将方法的参数，返回值，函数体内对类型，方法，字段的引用全部重定向到原始dll内对应的成员（lambda表达式类型及成员除外）
/// </summary>
public class MethodPatcher
{
    /// <summary>
    /// 遍历IL过程中发现的所有的不包含泛型参数 MethodDef 和 MethodSpec, 以及个别的 MemberRef(TypeSpec内定义的非泛型方法类型为MemberRef)。
    /// </summary>
    public HashSet<IMethod> allMethodsWithoutGeneric { get; private set; } = new HashSet<IMethod>();

    AssemblyDataForPatch _assemblyDataForPatch;
    Importer            _importer;
    AssemblyDef         _patchAssDef;
    TypeDef             _wrapperClass;

    public MethodPatcher(AssemblyDataForPatch assemblyData, Importer importer)
    {
        _assemblyDataForPatch = assemblyData;
        _importer = importer;
        _patchAssDef = _assemblyDataForPatch.patchDllData.moduleDef.Assembly;
        _wrapperClass = _assemblyDataForPatch.patchDllData.types[GlobalConfig.kWrapperClassFullName].definition;
    }

    public void PatchMethod(MethodDef methodDef, Dictionary<MethodDef, MethodFixStatus> processed, int depth)
    {
        if (processed.ContainsKey(methodDef))
            return;

        var fixStatus = new MethodFixStatus();
        processed.Add(methodDef, fixStatus);

        // 即使patch method无body也记录下来，也许原始dll内的定义是有body的，只有两边都无body最终才会跳过
        AddToAllMethodsWithoutGeneric(methodDef);

        if (!methodDef.HasBody)
            return;

        var sig = methodDef.ToString();
        if (_assemblyDataForPatch.baseDllData.allMethods.ContainsKey(sig))
            fixStatus.needHook = true;

        // 重映射参数
        foreach (var param in methodDef.Parameters)
        {
            param.Type = GetBaseTypeSig(param.Type);
        }

        //重映射返回值
        {
            methodDef.ReturnType = GetBaseTypeSig(methodDef.ReturnType);
        }
        
        // 重映射局部变量
        foreach (var local in methodDef.Body.Variables)
        {
            local.Type = GetBaseTypeSig(local.Type);
        }

        // 重映射函数Body内的指令
        var arrIns = methodDef.Body.Instructions.ToArray();

        for (int i = 0, imax = arrIns.Length; i < imax; i++)
        {
            Instruction ins = arrIns[i];
            if (ins.OpCode.OperandType == OperandType.InlineNone)
                continue;

            OperandType oprandType = ins.OpCode.OperandType;

            switch(ins.Operand)
            {
                /*
                 * TypeDef: 当前Scope(eg. Assembly、NestedType)范围内定义的类型，可能是非泛型或者未实例化的泛型类型
                 * TypeRef: 纯虚类 TypeRef 只有两个子类：TypeRefMD 和 TypeRefUser, 分别对应从metadata里读取的原有类型和用户后添加的类型
                 * TypeSpec: 泛型类的实例化对象
                 */
                case ITypeDefOrRef typeDefOrRef:
                    /*
                     * 其它dll内的非泛型实例直接跳过（eg. int, bool)
                     * 请注意！，泛型在其它dll定义但泛型参数在当前dll内时 DefinitionAssembly 也是其它dll, 例如 Action<MyClass> 的定义就在 mscorlib 内
                     * 另外，泛型参数(T,V,U) 等的类型为 TypeSpecMD
                     */
                    if ((typeDefOrRef is not TypeSpec) && typeDefOrRef.DefinitionAssembly != _patchAssDef)
                        break;

                    ins.Operand = GetBaseTypeOrRef(typeDefOrRef);
                    break;
                case FieldDef fieldDef:
                    ins.Operand = GetBaseFieldRef(fieldDef);
                    break;
                /*
                 * MethodRef, MemberRef(field ref in NestedClass)
                 * Field 引用定义在 MemberRef 里但是没有一个 FieldRef 类型就很奇怪
                 */
                case IMethodDefOrRef methodDefOrRef:
                    if (methodDefOrRef.IsField) // System.Int32 NS_Test.TestClsG`1/TestClsGInner`1<NS_Test.TestCls,NS_Test.TestDll_2>::innerField_i
                        ins.Operand = GetBaseFieldRef(methodDefOrRef as IField);
                    else
                    {
                        ins.Operand = GetBaseMethodRef(methodDefOrRef);
                    }

                    AddToAllMethodsWithoutGeneric(methodDefOrRef);
                    break;
                case MethodSpec methodSpec: // 泛型实例，（为什么 TypeSpec 继承自 ITypeDefOrRef， 但 MethodSpec 就不继承自 IMethodDefOrRef 呢？）
                    var sig1 = (methodSpec as IMethod).MethodSig;
                    ins.Operand = GetBaseMethodSpec(methodSpec);

                    AddToAllMethodsWithoutGeneric(methodSpec);
                    break;
                /*
                 * 此类型判断必须放在最后。对于引用，无法从类型区分字段还是方法, 因此只能使用if判断
                 * IMemberDef 继承自 IMemberRef，因此无需判断
                 * * 当前程序集内对其它类的成员变量的访问, 如果是相同Scope则是FieldDef, 否则是 MemberRef(NestedClass使用独立Scope) *
                 * 但 FieldRef 已被上面的 IMethodDefOrRef 处理
                 */
                case IMemberRef memberRef:
                    if (memberRef.IsMethod)
                    {
                        ins.Operand = GetBaseMethodRef(memberRef as IMethodDefOrRef);
                    }
                    else
                        throw new NotImplementedException($"类型引用尚未实现：{memberRef}");
                    break;
                default:
                    {
                        Type t = ins.Operand?.GetType();
                    }
                    break;
            } // switch
        } // for
    }

    /// <summary>
    /// 将不含泛型参数的方法添加到函数列表中
    /// </summary>
    /// <param name="method"></param>
    void AddToAllMethodsWithoutGeneric(IMethod method)
    {
        if (method.Name == ".cctor")
            return;

        var declType = method.DeclaringType;
        if (declType == _wrapperClass)
            return;

        // 此处 dnlib 疑似有bug，TypeDef.ContainsGenericParameter 总会返回false，即使是泛型类型,
        // 因此下面需要对 TypeDef 单独再判断一下 HasGenericParameters
        if (declType.DefinitionAssembly != _patchAssDef
            || declType.ContainsGenericParameter)
            return;

        if (declType is TypeDef td)
            if (td.HasGenericParameters)
                return;

        if(method is MethodDef methodDef)
        {
            if (!methodDef.HasGenericParameters)
                allMethodsWithoutGeneric.Add(methodDef);
        }
        else if(method is MemberRef memberRef)
        {
            var resolved = memberRef.Resolve();
            // 此种情况是 TypeSpec 内定义的非泛型方法
            if(declType is TypeSpec)
                allMethodsWithoutGeneric.Add(memberRef);
        }
        else if(method is MethodSpec methodSpec)
        {
            allMethodsWithoutGeneric.Add(methodSpec);
        }
    }


    static Dictionary<string, IField> s_baseFieldRefCache = new Dictionary<string, IField>();
    /// <summary>
    /// 获取 Base Assembly 内的字段引用
    /// </summary>
    /// <param name="fieldDefOrRef"></param>
    /// <returns></returns>
    IField GetBaseFieldRef(IField fieldDefOrRef)
    {
        string fullName = fieldDefOrRef.ToString();

        /*
        * 这是编译器自动生成的 lambda 表达式静态类
        * 其函数名和字段名是自动编号的，即使查找到同名同类型的成员也不一定对应的就是同一个对象
        */
        if (fullName.Contains("/<>c::"))
            return fieldDefOrRef;

        lock (s_baseFieldRefCache)
        {
            if (s_baseFieldRefCache.TryGetValue(fullName, out var cache))
                return cache;
        }

        IField ret = fieldDefOrRef;
        do
        {
            // 如果所在的类定义在原始dll内不存在(Patch新增的类)，则原样返回 fieldDef
            if (!_assemblyDataForPatch.baseDllData.types.ContainsKey(fieldDefOrRef.DeclaringType.ToString()))
                break;

            // patch dll 内定义的字段在原始dll内必须存在
            if (_assemblyDataForPatch.baseDllData.allMembers.TryGetValue(fullName, out var baseFieldRef))
                ret = _importer.Import(baseFieldRef as IField);
            else
                throw new Exception($"can not find field [{fieldDefOrRef}] in {_assemblyDataForPatch.baseDllData.name}");
        } while (false);

        lock (s_baseFieldRefCache)
            s_baseFieldRefCache.TryAdd(fullName, ret);

        return ret;
    }

    static Dictionary<string, IMethodDefOrRef> s_baseMethodRefCache = new Dictionary<string, IMethodDefOrRef>();
    /// <summary>
    /// 获取 Base Assembly 内的方法引用(非泛型方法实例)
    /// </summary>
    /// <param name="methodRef">方法定义或引用，此参数一定不是泛型方法实例，泛型方法实例的类型是 MethodSpec</param>
    /// <returns></returns>
    IMethodDefOrRef GetBaseMethodRef(IMethodDefOrRef methodDefOrRef)
    {
        /*
         * 对于Method来说，有两种数据，MethodDefOrRef, 和 MethodSig，后者定义了方法本身的签名(名字，参数，返回值等)，不包括其所属类型数据
         * 而前者包括后者及Class字段记录了其所属的类型，因此可以通过MethodSig在不同Module间查找或者定义方法
         */
        string fullName = methodDefOrRef.ToString();

        /*
        * 这是编译器自动生成的 lambda 表达式静态类
        * 其函数名和字段名是自动编号的，即使查找到同名同类型的成员也不一定对应的就是同一个对象
        */
        if (fullName.Contains("/<>c::"))
            return methodDefOrRef;

        lock (s_baseMethodRefCache)
        {
            if (s_baseMethodRefCache.TryGetValue(fullName, out var cache))
                return cache;
        }

        IMethodDefOrRef ret;
        do
        {
            // 如果类型定义可以在原始dll内找到，那么直接返回原始dll内对应的方法定义(可以是非泛型方法或者泛型方法的泛型定义)
            if (_assemblyDataForPatch.baseDllData.allMethods.TryGetValue(fullName, out var baseMethod))
            {
                ret = _importer.Import(baseMethod.definition) as IMethodDefOrRef;
                break;
            }

            /*
            * 如果所在的方法定义在原始dll内不存在
            * 首先判断其所属类型是否是泛型实例，
            * 如果是，则提取方法对应的泛型定义, 查看这个方法是否在原始dll内存在(有可能是泛型实例化类的非泛型方法, eg. TypeA<int>.FuncA())
            * 如果在原始方法内存在，则导入basedll内的泛型定义(导入泛型定义时会顺便导入其所在类型)，否则导入原始泛型定义
            * .net 不支持偏特化, 因此不存在泛型实例却带泛型参数方法的情况, 此处的 methodDefOrRef 一定没有泛型参数
            */

            var declType = methodDefOrRef.DeclaringType; // Nested Type 会显示全称
            var typeSig = declType.ToTypeSig();
            if (typeSig.IsGenericInstanceType)
            {
                // 在basedll中查找基于泛型类型的方法签名
                var genericMethodDef = methodDefOrRef.ResolveMethodDef(); // testClsG`1<T>.FuncA()
                if (_assemblyDataForPatch.baseDllData.allMethods.TryGetValue(genericMethodDef.ToString(), out var baseGenericMethod))
                    _importer.Import(baseGenericMethod.definition);
                else
                    _importer.Import(genericMethodDef);

                MemberRef memberRef = new MemberRefUser(_assemblyDataForPatch.baseDllData.moduleDef, methodDefOrRef.Name);
                memberRef.Signature = _importer.Import(methodDefOrRef.MethodSig);

                var genericInstSig = (declType.ToTypeSig() as GenericInstSig);
                memberRef.Class = BuildBaseGenericInstTypeSig(genericInstSig).ToTypeDefOrRef();
                ret = memberRef;
            }
            else
                ret = methodDefOrRef; // 新增方法或者非hotreload库内定义的方法(eg. corlib)
        } while (false);
        
        lock (s_baseMethodRefCache)
            s_baseMethodRefCache.TryAdd(fullName, ret);

        return ret;
    }

    static Dictionary<string, MethodSpec> s_baseMethodSpecCache = new Dictionary<string, MethodSpec>();
    /// <summary>
    /// 获取 Base Dll 内的泛型方法实例的定义
    /// </summary>
    /// <param name="methodSpec"></param>
    /// <returns></returns>
    MethodSpec GetBaseMethodSpec(MethodSpec methodSpec)
    {
        string fullName = methodSpec.ToString();
        lock(s_baseMethodSpecCache)
        {
            if (s_baseMethodSpecCache.TryGetValue(fullName, out var cache))
                return cache;
        }

        // 获取当前方法所在类型在 base dll 内对应的类型(有可能也是泛型实例[var])
        var baseType = GetBaseTypeOrRef(methodSpec.DeclaringType); // NS_Test.TestClsG`1<System.Single>

        // 构建当前方法的泛型参数实例(varM)
        var gArgs = methodSpec.GenericInstMethodSig.GenericArguments;
        TypeSig[] baseTypeSigs = new TypeSig[gArgs.Count];
        for(int i = 0, imax = baseTypeSigs.Length; i < imax; i++)
        {
            baseTypeSigs[i] = GetBaseTypeSig(gArgs[i]); // baseTypeSigs[0] = System.Boolean
        }

        GenericInstMethodSig gimg = new GenericInstMethodSig(baseTypeSigs);

        // 生成泛型方法引用
        var genericMethodSig = methodSpec.Method.MethodSig; // T <!!0>(T,U)
        // System.Single NS_Test.TestClsG`1<System.Single>::ShowGA<!!0>(System.Single,U)
        var baseGenericMethodRef = new MemberRefUser(_assemblyDataForPatch.baseDllData.moduleDef, methodSpec.Name, genericMethodSig, baseType);

        // 使用泛型方法引用和泛型参数创建泛型方法实例（定义）
        var ret = new MethodSpecUser(baseGenericMethodRef, gimg);

        lock (s_baseMethodSpecCache)
            s_baseMethodSpecCache.TryAdd(fullName, ret);

        return ret;
    }

    // 指定名称的类型在base dll 内的映射（或者创建的基于base dll的类型），使用string做为key的原因是有些地方会重复生成 TypeRef
    static Dictionary<string, ITypeDefOrRef> s_baseTypeOrRefCache = new Dictionary<string, ITypeDefOrRef>();

    ITypeDefOrRef GetBaseTypeOrRef(ITypeDefOrRef patchType)
    {
        var sig = patchType.ToTypeSig();
        if (sig.IsGenericTypeParameter || sig.IsGenericMethodParameter) // 泛型参数(T,V,U)不重定向
            return patchType;

        string fullName = patchType.ToString();

        /*
        * 这是编译器自动生成的 lambda 表达式静态类
        * 其函数名和字段名是自动编号的，即使查找到同名同类型的成员也不一定对应的就是同一个对象
        */
        if (fullName.Contains("<>c"))
            return patchType;

        ITypeDefOrRef ret;
        lock (s_baseTypeOrRefCache)
        {
            if (s_baseTypeOrRefCache.TryGetValue(fullName, out ret))
                return ret;
        }

        ret = GetBaseTypeSig(sig)?.ToTypeDefOrRef();
        Debug.Assert(ret != null);

        lock (s_baseTypeOrRefCache)
        {
            s_baseTypeOrRefCache.TryAdd(fullName, ret);
        }
        return ret;
    }

    static Dictionary<string, TypeSig> s_baseTypeSigCache = new Dictionary<string, TypeSig>();
    /// <summary>
    /// 获取BaseDll内对应的TypeSig（如果不在BaseDll内则原样返回）
    /// </summary>
    /// <param name="patchTypeSig"></param>
    /// <returns></returns>
    TypeSig GetBaseTypeSig(TypeSig patchTypeSig)
    {
        if (patchTypeSig.IsGenericTypeParameter || patchTypeSig.IsGenericMethodParameter) // 泛型参数(T,V,U)不重定向
            return patchTypeSig;

        string fullName = patchTypeSig.ToString();
        lock(s_baseTypeSigCache)
        {
            if (s_baseTypeSigCache.TryGetValue(fullName, out var cache))
                return cache;
        }

        TypeSig ret = null;
        if (_assemblyDataForPatch.baseDllData.types.TryGetValue(fullName, out var baseTypeData)) // 普通的 TypeDefSig，在 base dll 内有定义
            ret =_importer.Import(baseTypeData.typeSig);
        else if (patchTypeSig.IsGenericInstanceType)    // TypeSpec，泛型的实例化类型，依次拆开并重建
            ret = BuildBaseGenericInstTypeSig(patchTypeSig as GenericInstSig);
        else if (patchTypeSig is NonLeafSig nonLeafSig) // 有 []&* 等修饰符的类型，其有Next字段，需要遍历重新创建
        {
            List<NonLeafSig> lstSig = new List<NonLeafSig>();
            {
                var currSig = nonLeafSig;
                do
                {
                    lstSig.Add(currSig);
                    currSig = currSig.Next as NonLeafSig;
                } while (currSig != null);
            }

            TypeSig baseNakedSig = GetBaseTypeSig(lstSig[lstSig.Count - 1].Next);

            TypeSig nextSig = baseNakedSig;
            for(int i = lstSig.Count - 1; i >= 0; i--)
            {
                switch (nonLeafSig)
                {
                    case PtrSig:
                    case ByRefSig:
                    case SZArraySig:
                    case PinnedSig:
                        lstSig[i] = Activator.CreateInstance(nonLeafSig.GetType(), nextSig) as NonLeafSig;
                        break;
                    case ArraySig arrSig:
                        lstSig[i] = new ArraySig(nextSig, arrSig.Rank, arrSig.Sizes, arrSig.LowerBounds);
                        break;
                    case ModifierSig modifierSig: // CModReqdSig, CModOptSig
                        var baseModifier = GetBaseTypeSig(modifierSig.Modifier.ToTypeSig());
                        lstSig[i] = Activator.CreateInstance(modifierSig.GetType(), baseModifier, nextSig) as NonLeafSig;
                        break;
                    case ValueArraySig valArrSig:
                        lstSig[i] = new ValueArraySig(nextSig, valArrSig.Size);
                        break;
                    case ModuleSig moduleSig:
                        lstSig[i] = new ModuleSig(moduleSig.Index, nextSig);
                        break;
                    default:
                        throw new Exception($"invalid NonLeafSig:{nonLeafSig.GetType().FullName}");
                }

                nextSig = lstSig[i];
            }
            ret = lstSig[0];
        }
        else
            ret = patchTypeSig; // TypeRef 等类型，需要仔细检查是否还有遗漏

        Debug.Assert(ret != null);

        lock (s_baseTypeSigCache)
            s_baseTypeSigCache.TryAdd(fullName, ret);

        return ret;
    }


    static Dictionary<string, GenericInstSig> s_genericInstCache = new Dictionary<string, GenericInstSig>();
    /// <summary>
    /// 构建Patch GenericInstSigType 在Base dll内对应的类型
    /// </summary>
    /// <param name="genericType"></param>
    /// <param name="argTypeList"></param>
    /// <returns></returns>
    public GenericInstSig BuildBaseGenericInstTypeSig(GenericInstSig patchGISig)
    {
        string fullName = patchGISig.ToString();
        lock(s_genericInstCache)
        {
            if (s_genericInstCache.TryGetValue(fullName, out var cache))
                return cache;
        }

        ClassOrValueTypeSig genericType = patchGISig.GenericType;
        IAssembly scope = genericType.GetNonNestedTypeRefScope().DefinitionAssembly;
        if (scope == _assemblyDataForPatch.patchDllData.moduleDef.Assembly)
        {
            // 此时 genericType 指向的Type必定为TypeDef而不是TypeRef
            if (_assemblyDataForPatch.baseDllData.types.TryGetValue(genericType.ToString(), out var baseGTypeData))
                genericType = _importer.Import(baseGTypeData.typeSig) as ClassOrValueTypeSig;
            else
                throw new Exception($"can not find type:{genericType}");
        }

        TypeSig[] baseArgSigs = new TypeSig[patchGISig.GenericArguments.Count]; // NestedClass 会输出所有的泛型参数，因此无需递归
        for (int i = 0, imax = baseArgSigs.Length; i < imax; i++)
        {
            baseArgSigs[i] = GetBaseTypeSig(patchGISig.GenericArguments[i]);
        }

        var ret = new GenericInstSig(genericType, baseArgSigs);
        lock (s_genericInstCache)
            s_genericInstCache.TryAdd(fullName, ret);

        return ret;
    }
}
