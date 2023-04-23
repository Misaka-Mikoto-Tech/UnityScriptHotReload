/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System;
using System.Reflection;
using System.Linq;
using dnlib;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;

namespace AssemblyPatcher;

public static class Utils
{
    public static bool IsFilesEqual(string lpath, string rpath)
    {
        if (!File.Exists(lpath) || !File.Exists(rpath))
            return false;

        long lLen = new FileInfo(lpath).Length;
        long rLen = new FileInfo(rpath).Length;
        if (lLen != rLen)
            return false;

        return GetMd5OfFile(lpath) == GetMd5OfFile(rpath);
    }

    public static string GetMd5OfFile(string filePath)
    {
        string fileMd5 = null;
        try
        {
            using (var fs = File.OpenRead(filePath))
            {
                var md5 = MD5.Create();
                var md5Bytes = md5.ComputeHash(fs);
                fileMd5 = BitConverter.ToString(md5Bytes).Replace("-", "").ToLower();
            }
        }
        catch { }
        return fileMd5;
    }

    public static void RemoveAllFiles(string dir)
    {
        if (!Directory.Exists(dir))
            return;

        string[] files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
        foreach (var file in files)
            File.Delete(file);
    }

    public static BindingFlags BuildBindingFlags(MethodDef definition)
    {
        BindingFlags flags = BindingFlags.Default;
        if (definition.IsPublic)
            flags |= BindingFlags.Public;
        else
            flags |= BindingFlags.NonPublic;

        if (definition.IsStatic)
            flags |= BindingFlags.Static;
        else
            flags |= BindingFlags.Instance;

        return flags;
    }

    public static BindingFlags BuildBindingFlags(MethodBase methodInfo)
    {
        BindingFlags flags = BindingFlags.Default;
        if (methodInfo.IsPublic)
            flags |= BindingFlags.Public;
        else
            flags |= BindingFlags.NonPublic;

        if (methodInfo.IsStatic)
            flags |= BindingFlags.Static;
        else
            flags |= BindingFlags.Instance;

        return flags;
    }

    /// <summary>
    /// 以 MethodDefinition 为参数或者 MethodInfo
    /// </summary>
    /// <param name="t"></param>
    /// <param name="definition"></param>
    /// <returns></returns>
    /// <remarks>TODO 优化性能</remarks>
    public static MethodBase GetReflectMethodSlow(Type t, MethodDef definition)
    {
        var flags = BuildBindingFlags(definition);
        bool isConstructor = definition.IsConstructor;
        MethodBase[] mis = isConstructor ? (MethodBase[])t.GetConstructors(flags) : t.GetMethods(flags);

        // dnLib 会把 this 作为第一个参数
        Parameter[] defParaArr = definition.IsStatic ? definition.Parameters.ToArray() : definition.Parameters.Skip(1).ToArray();

        foreach(var mi in mis)
        {
            if (!isConstructor)
            {
                if (mi.Name != definition.Name)
                    continue;
                if (GetDnLibTypeName((mi as MethodInfo).ReturnType) != definition.ReturnType.FullName)
                    continue;
            }
            else if (mi.IsStatic != definition.IsStatic)
                continue;

            ParameterInfo[] piArr = mi.GetParameters();
            if(piArr.Length == defParaArr.Length)
            {
                bool found = true;
                for(int i = 0, imax = piArr.Length; i < imax; i++)
                {
                    var defPara = defParaArr[i];
                    var pi = piArr[i];

                    if (GetDnLibTypeName(pi.ParameterType) != defPara.Type.FullName)
                    {
                        found = false;
                        break;
                    }
                }

                if(found)
                    return mi;
            }
        }
        return null;
    }

    /// <summary>
    /// 从指定Assembly中查找特定签名的方法
    /// </summary>
    /// <param name="methodBase"></param>
    /// <param name="assembly"></param>
    /// <returns></returns>
    public static MethodBase GetMethodFromAssembly(MethodBase methodBase, Assembly assembly)
    {
        string typeName = methodBase.DeclaringType.FullName;
        Type t = assembly.GetType(typeName);
        if (t == null)
            return null;

        var flags = BuildBindingFlags(methodBase);
        string sig = methodBase.ToString();
        MethodBase[] mis = (methodBase is ConstructorInfo) ? (MethodBase[])t.GetConstructors(flags) : t.GetMethods(flags);

        foreach(var mi in mis)
        {
            if (mi.ToString() == sig)
                return mi;
        }
        return null;
    }

    public static bool IsLambdaStaticType(TypeDef typeDef)
    {
        return typeDef.ToString().EndsWith(GlobalConfig.Instance.lambdaWrapperBackend, StringComparison.Ordinal);
    }

    public static bool IsLambdaStaticType(string typeSignature)
    {
        return typeSignature.EndsWith(GlobalConfig.Instance.lambdaWrapperBackend, StringComparison.Ordinal);
    }

    public static bool IsLambdaMethod(MethodDef methodDef)
    {
        return methodDef.Name.StartsWith("<");
    }

    public static PdbDocument GetDocOfMethod(MethodDef methodDef)
    {
        if (!methodDef.HasBody || !methodDef.Body.HasInstructions)
            return null;

        return methodDef.Body.Instructions[0].SequencePoint?.Document; // 编译器自动生成的指令没有 SequencePoint
    }

    private static string GetDnLibTypeName(Type t)
    {
        if (t.ContainsGenericParameters)
        {
            return t.Name;
        }
        else
            return t.ToString().Replace('+', '/').Replace('[', '<').Replace(']', '>').Replace("<>", "[]"); // 最后一步是还原数组的[]
    }

    public static Dictionary<string, string> GetFallbackAssemblyPaths()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var ret = new Dictionary<string, string>();
        foreach(var ass in assemblies)
        {
            ret.TryAdd(Path.GetFileNameWithoutExtension(ass.Location), ass.Location);
        }
        return ret;
    }


    static string s_corlibLibName = null;
    static string s_corlibLibSig = null;
    static string s_systemLibName = typeof(System.Uri).Assembly.FullName;
    static string s_systemXmlLibName = typeof(System.Xml.XmlText).Assembly.FullName;

    static string s_systemLibSig = ", " + s_systemLibName;
    static string s_systemXmlLibSig = ", " + s_systemXmlLibName;
    static string s_defaultSig = ", Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";

    static void InitCorlibSigs(TypeSig typeDef)
    {
        if(s_corlibLibName == null)
        {
            s_corlibLibName = typeDef.Module.CorLibTypes.Void.DefinitionAssembly.FullName;
            s_corlibLibSig = ", " + s_corlibLibName;
        }
    }

    public static string GetRuntimeTypeName(TypeSig typeDef)
    {
        InitCorlibSigs(typeDef);

        string fullName = typeDef.ReflectionFullName;

        if (typeDef.Module == null) // T,U,V 之类的泛型参数
            return fullName;

        fullName = fullName.Replace(s_corlibLibSig, "").Replace(s_systemLibSig, "System").Replace(s_systemXmlLibSig, "System.Xml").Replace(s_defaultSig, "");
        fullName = fullName.Replace(", <<<NULL>>>", ""); // List<T> 会输出 "System.Collections.Generic.List`1[[T, <<<NULL>>>]]"

        if (!typeDef.DefinitionAssembly.IsCorLib())
            fullName += ", " + typeDef.DefinitionAssembly.Name;

        return fullName;
    }

    /// <summary>
    /// 递归检查一个方法中是否有泛型参数
    /// </summary>
    /// <param name="methodDef"></param>
    /// <returns></returns>
    public static bool IsGeneric(MethodDef methodDef)
    {
        if (methodDef.HasGenericParameters)
            return true;

        var type = methodDef.DeclaringType;
        while(type != null)
        {
            if (type.HasGenericParameters)
                return true;
            type = type.DeclaringType;
        }
        return false;
    }

    /// <summary>
    /// 获取原始dll对应的patch dll名称（无扩展名）
    /// </summary>
    /// <param name="baseDllName"></param>
    /// <returns></returns>
    public static string GetPatchDllName(string baseDllName)
    {
        string ret = string.Format(GlobalConfig.Instance.patchDllPathFormat, Path.GetFileNameWithoutExtension(baseDllName), GlobalConfig.Instance.patchNo);
        ret = Path.GetFileNameWithoutExtension(ret);
        return ret;
    }
}
