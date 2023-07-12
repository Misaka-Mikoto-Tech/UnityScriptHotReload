using Microsoft.CodeAnalysis;
using MonoHook;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;

namespace AssemblyPatcher;

/// <summary>
/// Roslyn 会校验一些可访问性和类型匹配性，但我们会在编译完成后的patcher步骤修正掉，因此无需考虑此类限制
/// 所以我们把这些校验都给去除
/// </summary>
internal unsafe class MonkeyHooks
{
    static bool s_inited = false;
    static MethodHook _hook_IsAtLeastAsVisibleAs;

    public delegate bool DeleVisibleCheck(object type, object sym, ref object useSiteInfo);

    public static void Init()
    {
        if (s_inited)
            return;

        {
            Type t = typeof(Microsoft.CodeAnalysis.CSharp.CSharpExtensions).Assembly.GetType("Microsoft.CodeAnalysis.CSharp.Symbols.TypeSymbolExtensions");
            var miTarget = t.GetMethod("IsAtLeastAsVisibleAs", BindingFlags.Static | BindingFlags.Public);

            var miReplace = new DeleVisibleCheck(IsAtLeastAsVisibleAs_Replace).Method;
            var miProxy = new DeleVisibleCheck(IsAtLeastAsVisibleAs_Proxy).Method;
            _hook_IsAtLeastAsVisibleAs = new MethodHook(miTarget, miReplace, null);
            _hook_IsAtLeastAsVisibleAs.Install();
        }

        s_inited = true;
    }

    static bool IsAtLeastAsVisibleAs_Replace(object /*TypeSymbol*/ type, object /*Symbol*/ sym, ref object /*CompoundUseSiteInfo<AssemblySymbol>*/ useSiteInfo)
    {
        string str = "it's replace";
        //Console.WriteLine(str);

        //var targetPtr = _hook_IsAtLeastAsVisibleAs.targetMethod.MethodHandle.GetFunctionPointer();
        //Console.WriteLine($"target mem: {HookUtils.HexToString(targetPtr.ToPointer(), 64, -16)}");

        //bool oriRet = IsAtLeastAsVisibleAs_Proxy(type, sym, ref useSiteInfo); // 目前不支持 .net6 下调用原始实现，等后续再实现此功能
        return true;
    }

    [MethodImpl(MethodImplOptions.NoOptimization | MethodImplOptions.NoInlining)]
    static bool IsAtLeastAsVisibleAs_Proxy(object /*TypeSymbol*/ type, object /*Symbol*/ sym, ref object /*CompoundUseSiteInfo<AssemblySymbol>*/ useSiteInfo)
    {
        string str2 = "it's proxy";
        Console.WriteLine(str2);
        string typeName = type.GetType().FullName;
        Debug.Log(typeName);
        return true;
    }
}
