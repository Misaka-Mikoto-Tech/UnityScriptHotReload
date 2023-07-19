using dnlib.DotNet;
using DotNetDetour;
using NHibernate.Id.Insert;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MonoHook
{
    /// <summary>
    /// 针对不同运行时后端的功能集合(mono, .net framework, .netcore, .net6 etc.)
    /// </summary>
    public abstract class RuntimeBackend
    {
        public static RuntimeBackend Create()
        {
            if(IsMono)
            {
                if(LDasm.IsIL2CPP())
                    return new RuntimeBackend_il2cpp();
                else
                    return new RuntimeBackend_mono();
            }
            else if(IsCore)
                return  new RuntimeBackend_net60();
            else
                return new RuntimeBackend_net_framework();
        }

        public static readonly bool IsMono =
            // This is what everyone expects.
            Type.GetType("Mono.Runtime") != null ||
            // .NET Core BCL running on Mono, see https://github.com/dotnet/runtime/blob/main/src/libraries/Common/tests/TestUtilities/System/PlatformDetection.cs
            Type.GetType("Mono.RuntimeStructs") != null;

        public static readonly bool IsCore =
            typeof(object).Assembly.GetName().Name == "System.Private.CoreLib";

        /// <summary>
        /// 获取真实的jit之后的函数地址
        /// </summary>
        /// <param name="handle"></param>
        /// <returns></returns>
        /// <remarks>某些平台会有桩函数(precode), method.MethodHandle.GetFunctionPointer() 获取到的并不是真实的函数地址</remarks>
        public virtual IntPtr GetRealFunctionPointer(MethodBase method) { return method.MethodHandle.GetFunctionPointer(); }
        public virtual void PrepareMethod(MethodBase method) { RuntimeHelpers.PrepareMethod(method.MethodHandle); }
        public virtual void PrepareMethod(MethodBase method, RuntimeTypeHandle[] instantiation) { RuntimeHelpers.PrepareMethod(method.MethodHandle, instantiation); }
        public virtual void DisableInlining(MethodBase method, RuntimeMethodHandle handle) { }
    }

    public unsafe class RuntimeBackend_mono : RuntimeBackend
    {
        public override void DisableInlining(MethodBase method, RuntimeMethodHandle handle)
        {
            // https://github.com/mono/mono/blob/34dee0ea4e969d6d5b37cb842fc3b9f73f2dc2ae/mono/metadata/class-internals.h#L64
            ushort* iflags = (ushort*)((long)handle.Value + 2);
            *iflags |= (ushort)MethodImplOptions.NoInlining;
        }
    }

    public class RuntimeBackend_il2cpp: RuntimeBackend { }

    public unsafe class RuntimeBackend_NET : RuntimeBackend
    {
        /*
         * .net 和 .net6 的 methodHandle.GetFunctionPointer() 获取到的是桩代码，因此需要拦截 CILJit::compileMethod 获取真实地址
         */
        public override IntPtr GetRealFunctionPointer(MethodBase method)
        {
            
            return IntPtr.Zero;
        }
    }

    public class RuntimeBackend_net_framework : RuntimeBackend_NET { }
    public class RuntimeBackend_net60 : RuntimeBackend_NET
    {
        public override unsafe void DisableInlining(MethodBase method, RuntimeMethodHandle handle)
        {
            // https://github.com/dotnet/runtime/blob/89965be3ad2be404dc82bd9e688d5dd2a04bcb5f/src/coreclr/src/vm/method.hpp#L178
            // mdcNotInline = 0x2000
            // References to RuntimeMethodHandle (CORINFO_METHOD_HANDLE) pointing to MethodDesc
            // can be traced as far back as https://ntcore.com/files/netint_injection.htm

            const int offset =
                2 // UINT16 m_wFlags3AndTokenRemainder
              + 1 // BYTE m_chunkIndex
              + 1 // BYTE m_chunkIndex
              + 2 // WORD m_wSlotNumber
              ;
            ushort* m_wFlags = (ushort*)(((byte*)handle.Value) + offset);
            *m_wFlags |= 0x2000;
        }
    }
}
