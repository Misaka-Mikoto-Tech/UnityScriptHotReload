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

    private MethodPatcher       _methodPatcher;

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

        _methodPatcher = new MethodPatcher(assemblyDataForPatch);

        FixNewAssembly();
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

    public void WriteToFile()
    {
        string patchPath = assemblyDataForPatch.patchDllData.moduleDef.Location;
        string fixPath = $"{Path.GetDirectoryName(patchPath)}/{Path.GetFileNameWithoutExtension(patchPath)}_fix.dll";
        string patchPdbPath = Path.ChangeExtension(patchPath, ".pdb");
        string fixPdbPath = Path.ChangeExtension(fixPath, ".pdb");


        var opt = new ModuleWriterOptions(assemblyDataForPatch.patchDllData.moduleDef) { WritePdb = true };
        assemblyDataForPatch.patchDllData.moduleDef.Write(fixPath, opt);
        return;
        // 重命名 dll 名字
        assemblyDataForPatch.patchDllData.Unload();
        File.Delete(patchPath);
        File.Delete(patchPdbPath);
        File.Move(fixPath, patchPath);
        File.Move(fixPdbPath, patchPdbPath);
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

