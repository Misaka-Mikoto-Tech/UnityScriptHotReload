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

using System.Security.Permissions;
using SecurityAction = System.Security.Permissions.SecurityAction;
using dnlib.DotNet.Emit;
using NHibernate.Mapping;
using TypeDef = dnlib.DotNet.TypeDef;
using dnlib.DotNet.MD;
using Remotion.Linq.Parsing.Structure.NodeTypeProviders;
using dnlib.DotNet.Writer;
using System.Diagnostics;
using MethodImplAttributes = dnlib.DotNet.MethodImplAttributes;
using System.Xml.Linq;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using NHibernate.Mapping.ByCode;
using System.Runtime.Loader;

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

    private CorLibTypeSig   _voidTypeSig;
    private CorLibTypeSig   _int32TypeSig;
    private CorLibTypeSig   _stringTypeSig;
    private CorLibTypeSig   _objectTypeSig;
    private TypeSig         _typeTypeSig;
    private TypeSig         _typeArrayTypeSig;
    private TypeSig         _methodBaseTypeSig;
    private IMethodDefOrRef _getMethodFromHandle_2;

    private TypeDef         _wrapperClass;

    private Importer            _importer;
    private MethodPatcher       _methodPatcher;
    private GenericInstScanner  _genericInstScanner;

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
        _voidTypeSig = patchDllDef.CorLibTypes.Void;
        _int32TypeSig = patchDllDef.CorLibTypes.Int32;
        _stringTypeSig = patchDllDef.CorLibTypes.String;
        _objectTypeSig = patchDllDef.CorLibTypes.Object;

        var mbType = patchDllDef.CorLibTypes.GetTypeRef("System.Reflection", "MethodBase").Resolve();

        {// 使用2个参数的重载
            var mi = from m in mbType.FindMethods("GetMethodFromHandle") where m.GetParamCount() == 2 select m;
            _getMethodFromHandle_2 = patchDllDef.Import(mi.First());
        }

        _typeTypeSig = patchDllDef.Import(typeof(Type)).ToTypeSig();
        _typeArrayTypeSig = new SZArraySig(_typeTypeSig);
        

        _ctorGenericMethodIndex = patchDllDef.Import(_typeGenericMethodIndex.definition.FindDefaultConstructor());
        _ctorGenericMethodWrapper = patchDllDef.Import(_typeGenericMethodWrapper.definition.FindDefaultConstructor());

        _importer = new Importer(assemblyDataForPatch.patchDllData.moduleDef);
        _methodBaseTypeSig = _importer.ImportAsTypeSig(typeof(MethodBase));

        _methodPatcher = new MethodPatcher(assemblyDataForPatch, _importer);
        _genericInstScanner = new GenericInstScanner(assemblyDataForPatch, _importer);

        _wrapperClass = assemblyDataForPatch.patchDllData.types[GlobalConfig.kWrapperClassFullName].definition;

        // 扫描原始 dll 中的所有泛型实例
        _genericInstScanner.Scan();

        FixNewAssembly();
        GenGenericMethodWrappers();
        GenRuntimeMethodsGetter();
        isValid = true;
        return isValid;
    }
    
    void FixNewAssembly()
    {
        int patchNo = GlobalConfig.Instance.patchNo;

        var processed = new Dictionary<MethodDef, MethodFixStatus>();
        foreach (var (_, methodData) in assemblyDataForPatch.patchDllData.allMethods)
        {
            /* 尝试把类型改为 interface, 以规避 mono stack walk 时从 this_obj->vtable 读取类型导致不一致的问题
             * 详见 https://github.com/Unity-Technologies/mono/blob/unity-2021.3-mbe/mono/mini/mini-exceptions.c#L835
             */ 
             /* method = jinfo_get_method(ji);
            //if (mono_method_get_context(method)->method_inst || mini_method_is_default_method(method)) // 伪装成接口的默认实现
            //{
            //    /* A MonoMethodRuntimeGenericContext* */
            //    return info;
            //}
            //else if ((method->flags & METHOD_ATTRIBUTE_STATIC) || m_class_is_valuetype(method->klass))
            //{
            //    /* A MonoVTable* */
            //    return info;
            //}
            //else
            //{
            //    /* Avoid returning a managed object */
            //    MonoObject* this_obj = (MonoObject*)info;

            //    return this_obj ? this_obj->vtable : NULL; // 走到这里就完蛋了
            //}

            methodData.definition.DeclaringType.Attributes |= dnlib.DotNet.TypeAttributes.Interface;
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
        foreach(var genMethodData in _genericInstScanner.genericMethodDatas)
        {
            AddCAGenericIndex(genMethodData.genericMethodInPatch, _wrapperIndex);
            var genInstArgs = genMethodData.genericInsts;
            for (int i = 0, imax = genInstArgs.Count; i < imax; i++)
            {
                var typeGenArgs = genInstArgs[i].typeGenArgs;
                var methodGenArgs = genInstArgs[i].methodGenArgs;

                var (wrapperMethod, instTarget) = Utils.GenWrapperMethodBody(genMethodData.genericMethodInPatch, _wrapperIndex, i, _importer, _wrapperClass, typeGenArgs, methodGenArgs);
                AddCAGenericMethodWrapper(wrapperMethod, instTarget, _wrapperIndex, typeGenArgs, methodGenArgs);
                genInstArgs[i].wrapperMethodDef = wrapperMethod; // 记录 wrapperMethodDef 定义
                genInstArgs[i].instMethodInPatch = instTarget;
            }
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
    void AddCAGenericMethodWrapper(MethodDef wrapperMethod, IMethod instTarget, int idx, IList<TypeSig> typeGenArgs, IList<TypeSig> methodGenArgs)
    {
        List<CAArgument> caTypes = new List<CAArgument>();

        foreach (var t in typeGenArgs)
        {
            caTypes.Add(new CAArgument(_typeTypeSig, t));
        }

        foreach (var t in methodGenArgs)
        {
            caTypes.Add(new CAArgument(_typeTypeSig, t));
        }

        var argTypes = new CAArgument(_typeArrayTypeSig, caTypes);
        var nameArgTypes = new CANamedArgument(true, _typeArrayTypeSig, "typeGenArgs", argTypes);

        AddCAGenericMethodWrapper(wrapperMethod, instTarget, idx, nameArgTypes);
    }

    void AddCAGenericMethodWrapper(MethodDef wrapperMethod, IMethod instTarget, int idx, CANamedArgument typeArgs)
    {
        var argIdx = new CAArgument(_int32TypeSig, idx);
        var nameArgIdx = new CANamedArgument(true, _int32TypeSig, "index", argIdx);

        var instArg = new CAArgument(_methodBaseTypeSig, instTarget);
        var nameInstArg = new CANamedArgument(true, _methodBaseTypeSig, "genericInstMethod", instArg);
        //var caArgs  = new CANamedArgument[] { nameArgIdx, nameInstArg, typeArgs };
        var caArgs  = new CANamedArgument[] { nameArgIdx, typeArgs };
        var ca = new CustomAttribute(_ctorGenericMethodWrapper, caArgs);
        wrapperMethod.CustomAttributes.Add(ca);
    }

    public class DictionaryDefInfos
    {
        public MethodDef genFuncDef;

        public TypeSig  dicTypeSig;
        public IMethod  dicCtor;
        public IMethod  dicAdd;
    }

    DictionaryDefInfos GetDictionaryDefInfos()
    {
        var ret = new DictionaryDefInfos();
        ret.genFuncDef = _wrapperClass.FindMethod("GetGenericInstMethodForPatch");
        ret.dicTypeSig = ret.genFuncDef.ReturnType; // Dictionary<MethodBase, MethodBase>

        TypeSpec dicType = ret.dicTypeSig.ToTypeDefOrRef() as TypeSpec;

        var genericDicType = dicType.ResolveTypeDef();
        var genericDicCtor = genericDicType.FindDefaultConstructor();
        var genericDicAdd = genericDicType.FindMethod("Add");

        ret.dicCtor = new MemberRefUser(dicType.Module, ".ctor", genericDicCtor.MethodSig, dicType);
        ret.dicAdd = new MemberRefUser(dicType.Module, "Add", genericDicAdd.MethodSig, dicType);
        return ret;
    }

    /// <summary>
    /// 生成运行时动态获取Base Dll内的非泛型和泛型实例方法与Patch Dll内的Wrapper方法关联的函数
    /// </summary>
    void GenRuntimeMethodsGetter()
    {
        var dicDefInfos = GetDictionaryDefInfos();
        var genFuncDef = dicDefInfos.genFuncDef;

        genFuncDef.Body = new CilBody();
        genFuncDef.Body.MaxStack = 8;
        var instructions = genFuncDef.Body.Instructions;

        instructions.Add(Instruction.Create(OpCodes.Newobj, dicDefInfos.dicCtor));    // newobj Dictionary<MethodInfo, MethodInfo>.ctor()

        // 为patch dll内发生改变文件内定义的非泛型方法生成wrapper映射
        {
            var allBaseMethods = assemblyDataForPatch.baseDllData.allMethods;
            foreach (var patchMethodData in _genericInstScanner.nonGenericMethodInPatch)
            {
                if (!allBaseMethods.TryGetValue(patchMethodData.fullName, out var baseMethodData))
                    continue;

                var imporedBaseMethod = _importer.Import(baseMethodData.definition);
                var importedBaseType = _importer.Import(baseMethodData.definition.DeclaringType);

                instructions.Add(Instruction.Create(OpCodes.Dup));                                  // dup  (dicObj->this)
                instructions.Add(Instruction.Create(OpCodes.Ldtoken, imporedBaseMethod));           // ldtoken key
                instructions.Add(Instruction.Create(OpCodes.Ldtoken, importedBaseType));            // ldtoken key_type
                instructions.Add(Instruction.Create(OpCodes.Call, _getMethodFromHandle_2));         // call GetMethodFromHandle
                instructions.Add(Instruction.Create(OpCodes.Ldtoken, patchMethodData.definition));  // ldtoken value
                instructions.Add(Instruction.Create(OpCodes.Ldtoken, patchMethodData.definition.DeclaringType));    // ldtoken _wrapperClass
                instructions.Add(Instruction.Create(OpCodes.Call, _getMethodFromHandle_2));         // call GetMethodFromHandle
                instructions.Add(Instruction.Create(OpCodes.Callvirt, dicDefInfos.dicAdd));         // callvirt Add
            }
        }
        
        // 为泛型方法生成wrapper映射
        foreach (var genericMethodData in _genericInstScanner.genericMethodDatas)
        {
            foreach(var instArgs in genericMethodData.genericInsts)
            {
                var importedBaseInstMethod = _importer.Import(instArgs.instMethodInBase);
                var importedBaseType = _importer.Import(instArgs.instMethodInBase.DeclaringType);
                //var patchType = instArgs.instMethodInPatch.DeclaringType;

                instructions.Add(Instruction.Create(OpCodes.Dup));                                  // dup  (dicObj->this)
                instructions.Add(Instruction.Create(OpCodes.Ldtoken, importedBaseInstMethod));      // ldtoken key
                instructions.Add(Instruction.Create(OpCodes.Ldtoken, importedBaseType));            // ldtoken key_type
                instructions.Add(Instruction.Create(OpCodes.Call, _getMethodFromHandle_2));         // call GetMethodFromHandle
                instructions.Add(Instruction.Create(OpCodes.Ldtoken, instArgs.wrapperMethodDef));   // ldtoken value
                instructions.Add(Instruction.Create(OpCodes.Ldtoken, _wrapperClass));               // ldtoken _wrapperClass
                //instructions.Add(Instruction.Create(OpCodes.Ldtoken, instArgs.instMethodInPatch));  // ldtoken value
                //instructions.Add(Instruction.Create(OpCodes.Ldtoken, patchType));                   // ldtoken patchType
                instructions.Add(Instruction.Create(OpCodes.Call, _getMethodFromHandle_2));         // call GetMethodFromHandle
                instructions.Add(Instruction.Create(OpCodes.Callvirt, dicDefInfos.dicAdd));         // callvirt Add
            }
        }

        instructions.Add(Instruction.Create(OpCodes.Ret));                                          // ret
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