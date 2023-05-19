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
using System.Linq;
using System.Text;
using SimpleJSON;
using dnlib;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;

using static AssemblyPatcher.Utils;
using System.Security.Permissions;
using SecurityAction = System.Security.Permissions.SecurityAction;
using dnlib.DotNet.Emit;
using NHibernate.Mapping;
using TypeDef = dnlib.DotNet.TypeDef;
using dnlib.DotNet.MD;
using Remotion.Linq.Parsing.Structure.NodeTypeProviders;
using dnlib.DotNet.Writer;
using System.Diagnostics;

namespace AssemblyPatcher;

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
public class AssemblyPatcher
{
    /// <summary>
    /// 是否合法，要求baseAssDef中存在的类型和方法签名在newAssDef中必须存在，但newAssDef可以存在新增的类型和方法
    /// </summary>
    public bool isValid { get; private set; }
    public string moduleName { get; private set; }
    public AssemblyDataForPatch assemblyDataForPatch { get; private set; }

    private static TypeData _typeGenericMethodIndex, _typeGenericMethodWrapper;

    private MemberRef _ctorGenericMethodIndex, _ctorGenericMethodWrapper;

    private CorLibTypeSig   _int32TypeSig;
    private CorLibTypeSig   _stringTypeSig;
    private CorLibTypeSig   _objectTypeSig;
    private TypeSig         _typeTypeSig;
    private TypeSig         _typeArrayTypeSig;

    private MethodPatcher       _methodPatcher;

    static AssemblyPatcher()
    {
        var shareCode = ModuleDefPool.GetModuleData("ShareCode");
        shareCode.types.TryGetValue("ScriptHotReload.GenericMethodIndexAttribute", out _typeGenericMethodIndex);
        shareCode.types.TryGetValue("ScriptHotReload.GenericMethodWrapperAttribute", out _typeGenericMethodWrapper);
    }

    public AssemblyPatcher(string moduleName)
    {
        this.moduleName = moduleName;
    }

    public bool DoPatch()
    {
        assemblyDataForPatch = new AssemblyDataForPatch(moduleName);
        assemblyDataForPatch.Init();

        if (!assemblyDataForPatch.isValid)
            return false;

        var patchDllDef = assemblyDataForPatch.patchDllData.moduleDef;
        _int32TypeSig = patchDllDef.CorLibTypes.Int32;
        _stringTypeSig = patchDllDef.CorLibTypes.String;
        _objectTypeSig = patchDllDef.CorLibTypes.Object;
        _typeTypeSig = patchDllDef.Import(typeof(Type)).ToTypeSig();
        _typeArrayTypeSig = new SZArraySig(_typeTypeSig);

        _ctorGenericMethodIndex = patchDllDef.Import(_typeGenericMethodIndex.definition.FindDefaultConstructor());
        _ctorGenericMethodWrapper = patchDllDef.Import(_typeGenericMethodWrapper.definition.FindDefaultConstructor());

        _methodPatcher = new MethodPatcher(assemblyDataForPatch);

        FixNewAssembly();
        GenGenericMethodWrappers();
        isValid = true;
        return isValid;
    }
    
    void FixNewAssembly()
    {
        int patchNo = GlobalConfig.Instance.patchNo;

        var processed = new Dictionary<MethodDef, MethodFixStatus>();
        foreach (var (_, methodData) in assemblyDataForPatch.patchDllData.allMethods)
        {
            _methodPatcher.PatchMethod(methodData.definition, processed, 0);
        }

        // 已存在类的静态构造函数需要清空，防止被二次调用
        if (processed.Count > 0)
        {
            var fixedType = new HashSet<TypeDef>();
            foreach(var kv in processed)
            {
                var status = kv.Value;
                if (status.ilFixed || status.needHook)
                    fixedType.Add(kv.Key.DeclaringType);
            }

            if(fixedType.Count > 0)
            {
                var constructors = new List<MethodDef>();
                var lambdaWrapperBackend = GlobalConfig.Instance.lambdaWrapperBackend;
                foreach (var tdef in fixedType)
                {
                    /*
                    * 这是编译器自动生成的 lambda 表达式静态类
                    * 由于代码修正时不会重定向其引用(自动编号的成员名称无法精确匹配)，因此需要保留其静态函数初始化代码
                    */
                    if (tdef.FullName.Contains("<>c"))
                        continue;

                    // 新定义的类型静态构造函数即使执行也是第一次执行，因此逻辑只能修正不能移除
                    if (assemblyDataForPatch.addedTypes.ContainsKey(tdef.FullName))
                        continue;

                    foreach(var mdef in tdef.Methods)
                    {
                        if (mdef.IsConstructor && mdef.IsStatic && mdef.HasBody)
                            constructors.Add(mdef);
                    }
                }
                StaticConstructorsQuickReturn(constructors);
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
    /// 修正被Hook或者被Fix的类型的静态构造函数，将它们改为直接返回, 否则其逻辑会在Patch里再次执行导致逻辑错误
    /// </summary>
    /// <param name="constructors"></param>
    void StaticConstructorsQuickReturn(List<MethodDef> constructors)
    {
        foreach (var ctor in constructors)
        {
            if (ctor.Name != ".cctor" || !ctor.HasBody)
                continue;

            // 直接移除会导致pdb找不到指令，因此直接在指令最前面插入一个ret指令
            var ins = ctor.Body.Instructions;
            ins.Insert(0, OpCodes.Ret.ToInstruction());
        }
    }

    int _wrapperIndex = 0;
    /// <summary>
    /// 为泛型方法生成wrapper函数，以避免Hook后StackWalk时crash
    /// </summary>
    void GenGenericMethodWrappers()
    {
        // 创建wrapper函数所在的类 ScriptHotReload.<>__GenericInstWrapper__
        var wrapperClass = new TypeDefUser("ScriptHotReload", "<>__GenericInstWrapper__");
        wrapperClass.IsAbstract = true; // static class
        wrapperClass.IsSealed = true;
        
        assemblyDataForPatch.patchDllData.moduleDef.Types.Add(wrapperClass);

        List<TypeSig> typeSigs = new List<TypeSig>();
        for (int i = 0; i < 6; i++) // TODO 测试代码，先全部填充 object, 将来改成扫描dll内所有的实例化参数
            typeSigs.Add(_objectTypeSig);

        foreach (var (_, methodData) in assemblyDataForPatch.patchDllData.allMethods)
        {
            var method = methodData.definition;
            if (!method.HasGenericParameters)
                continue;

            AddCAGenericIndex(method, _wrapperIndex);
            var wrapperMethod = Utils.GenWrapperMethodBody(method, _wrapperIndex, wrapperClass, typeSigs, typeSigs);
            AddCAGenericMethodWrapper(wrapperMethod, _wrapperIndex, typeSigs.ToArray());

            _wrapperIndex++;
        }
    }

    /// <summary>
    /// 给泛型方法添加 [GenericMethodIndex]
    /// </summary>
    /// <param name="method"></param>
    /// <param name="idx"></param>
    void AddCAGenericIndex(MethodDef method, int idx)
    {
        var argIdx = new CAArgument(_int32TypeSig, idx);
        var nameArgIdx = new CANamedArgument(true, _int32TypeSig, "index", argIdx);
        var ca = new CustomAttribute(_ctorGenericMethodIndex, new CANamedArgument[] { nameArgIdx });
        method.CustomAttributes.Add(ca);
    }

    /// <summary>
    /// 给wrapper方法添加 [GenericMethodWrapper]
    /// </summary>
    void AddCAGenericMethodWrapper(MethodDef method, int idx, TypeSig[] types)
    {
        CANamedArgument nameArgTypes = null;
        if (types != null && types.Length > 0)
        {
            List<CAArgument> caTypes = new List<CAArgument>();
            foreach (var t in types)
            {
                caTypes.Add(new CAArgument(_typeTypeSig, t));
            }
            var argTypes = new CAArgument(_typeArrayTypeSig, caTypes);
            nameArgTypes = new CANamedArgument(true, _typeArrayTypeSig, "typeGenArgs", argTypes);
        }

        AddCAGenericMethodWrapper(method, idx, nameArgTypes);
    }

    void AddCAGenericMethodWrapper(MethodDef method, int idx, CANamedArgument typeArgs)
    {
        var argIdx = new CAArgument(_int32TypeSig, idx);
        var nameArgIdx = new CANamedArgument(true, _int32TypeSig, "index", argIdx);
        var caArgs  = new CANamedArgument[] { nameArgIdx, typeArgs };
        var ca = new CustomAttribute(_ctorGenericMethodWrapper, caArgs);
        method.CustomAttributes.Add(ca);
    }

    public void WriteToFile()
    {
        string patchPath = assemblyDataForPatch.patchDllData.moduleDef.Location;
        string patchPdbPath = Path.ChangeExtension(patchPath, ".pdb");

        string tmpPath = $"{Path.GetDirectoryName(patchPath)}/tmp_{new Random().Next(100)}.dll";
        string tmpPdbPath = Path.ChangeExtension(tmpPath, ".pdb");

        var opt = new ModuleWriterOptions(assemblyDataForPatch.patchDllData.moduleDef) { WritePdb = true };
        assemblyDataForPatch.patchDllData.moduleDef.Write(tmpPath, opt);

        // 重命名 dll 名字
        assemblyDataForPatch.patchDllData.Unload();
        File.Delete(patchPath);
        File.Delete(patchPdbPath);
        File.Move(tmpPath, patchPath);
        File.Move(tmpPdbPath, patchPdbPath);
    }
}

//class TypeDefComparer<T> : IComparer<T> where T : MemberRef
//{
//    public static TypeDefComparer<T> comparer = new TypeDefComparer<T>();

//    public int Compare(T x, T y)
//    {
//        return x.FullName.CompareTo(y.FullName);
//    }
//}

