using dnlib.DotNet;
using DotNetDetour;
using NHibernate.Id.Insert;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        public virtual void DisableInlining() { }
    }

    public class RuntimeBackend_mono : RuntimeBackend
    {

    }

    public class RuntimeBackend_il2cpp: RuntimeBackend { }
    public class RuntimeBackend_net_framework : RuntimeBackend { }
    public class RuntimeBackend_net60 : RuntimeBackend { }
}
