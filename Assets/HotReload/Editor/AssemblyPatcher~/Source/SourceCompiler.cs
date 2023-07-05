/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */

#define COMPILE_WITH_ROSLYN

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace AssemblyPatcher;
public class SourceCompiler
{
    const int kMaxCompileTime = 60 * 1000;

    public string moduleName { get; private set; }
    public string outputPath { get; private set; }

    private string _rspPath;
    private static string s_CS_File_Path__Patch_Assembly_Attr__;            // __Patch_Assembly_Attr__.cs
    private static string s_CS_File_Path__Methods_For_Patch_Wrapper__Gen__; // __Methods_For_Patch_Wrapper__Gen__.cs

    private List<string> _filesToCompile = new List<string>();
    private PartialClassScanner _partialClassScanner;
    private List<SyntaxTree> _syntaxTrees;

    static SourceCompiler()
    {
        s_CS_File_Path__Patch_Assembly_Attr__ = GlobalConfig.Instance.tempScriptDir + $"/__Patch_Assembly_Attr__.cs";
        s_CS_File_Path__Methods_For_Patch_Wrapper__Gen__ = GlobalConfig.Instance.tempScriptDir + $"/__Methods_For_Patch_Wrapper__Gen__.cs";

        GenCSFile__Methods_For_Patch_Wrapper__Gen__();
    }
    
    public SourceCompiler(string moduleName)
    {
        this.moduleName = moduleName;
        outputPath = string.Format(GlobalConfig.Instance.patchDllPathFormat, this.moduleName, GlobalConfig.Instance.patchNo);
    }
    
    public int DoCompile()
    {
        Utils.DeleteFileWithRetry(outputPath);
        Utils.DeleteFileWithRetry(Path.ChangeExtension(outputPath, ".pdb"));

        _rspPath = GlobalConfig.Instance.tempScriptDir + $"/__{moduleName}_Patch.rsp";

        GenCSFile__Patch_Assembly_Attr__();

        GetAllFilesToCompile();
        
        int retCode = 0;

        Stopwatch sw = new Stopwatch();
        sw.Start();
#if COMPILE_WITH_ROSLYN
        retCode = CompilePatchDllWithRoslyn() ? 0 : -1;
        sw.Stop();
        Console.WriteLine($"Roslyn编译耗时: {sw.ElapsedMilliseconds}ms");
#else
        GenRspFile();
        retCode = RunDotnetCompileProcess();
        sw.Stop();
        Console.WriteLine($"csc编译耗时: {sw.ElapsedMilliseconds}ms");
#endif

        return retCode;
    }

    /// <summary>
    /// 创建文件 __Patch_GenericInst_Wrapper__Gen__.cs
    /// </summary>
    static void GenCSFile__Methods_For_Patch_Wrapper__Gen__()
    {
        string text =
@"using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ScriptHotReload
{
    /// <summary>
    /// 用于 AssemblyPatcher 生成非泛型和泛型实例定义的 wrapper 类型
    /// </summary>
    public class __Methods_For_Patch_Wrapper__Gen__
    {
        /// <summary>
        /// 扫描 base dll 里所有的方法，然后获取与之关联的 patch dll 内创建的 wrapper 函数
        /// </summary>
        /// <returns></returns>
        public static Dictionary<MethodBase, MethodBase> GetMethodsForPatch()
        {
            // 函数体会被 Assembly Patcher 替换
            throw new NotImplementedException();
        }

        /// <summary>
        /// 当 patch dll 内所有的函数均未定义局部变量时，#Strings heap 不会被dnlib写入pdb文件中，但mono认为此heap必定存在，且会去检验，这会导致crash
        /// 因此我们这里放一个无用的局部变量强制创建 #Strings heap
        /// </summary>
        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static string ___UnUsed_Method_To_Avoid_Dnlib_Bug___(string str)
        {
            string uselessVar = str + ""This is a useless variable used to avoid dnlib's bug, please don't remove it!"";
            return uselessVar;
        }
    }
}
";
        File.WriteAllText(s_CS_File_Path__Methods_For_Patch_Wrapper__Gen__, text, Encoding.UTF8);
    }

    /// <summary>
    /// 创建文件 __Patch_Assembly_Attr__.cs
    /// </summary>
    void GenCSFile__Patch_Assembly_Attr__()
    {
        string text =
@"using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Permissions;

[assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations | DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints | DebuggableAttribute.DebuggingModes.EnableEditAndContinue)]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
[assembly: IgnoresAccessChecksTo(""@{{MoudeleName}}"")]

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName)
        {
            AssemblyName = assemblyName;
        }

        public string AssemblyName { get; }
    }
}
";
        text = text.Replace("@{{MoudeleName}}", moduleName);

        File.WriteAllText(s_CS_File_Path__Patch_Assembly_Attr__, text, Encoding.UTF8);
    }

    void GenRspFile()
    {
        var sb = new StringBuilder();
        sb.AppendLine("-target:library");
        sb.AppendLine($"-out:\"{outputPath}\"");
        foreach(var def in GlobalConfig.Instance.defines)
            sb.AppendLine($"-define:{def}");
        foreach(var @ref in GlobalConfig.Instance.assemblyPathes.Values)
            sb.AppendLine($"-r:\"{@ref}\"");

#if FOR_NET6_0_OR_GREATER
        sb.AppendLine($"-r:\"{typeof(IgnoresAccessChecksToAttribute).Assembly.Location}\"");
#endif
        sb.AppendLine($"-langversion:latest");

        sb.AppendLine("/unsafe");
        sb.AppendLine("/deterministic");
        sb.AppendLine("/optimize-");
        sb.AppendLine("/debug:portable");
        sb.AppendLine("/nologo");
        sb.AppendLine("/RuntimeMetadataVersion:v4.0.30319");

        sb.AppendLine("/nowarn:0169");
        sb.AppendLine("/nowarn:0649");
        sb.AppendLine("/nowarn:1701");
        sb.AppendLine("/nowarn:1702");
        // obsolete warning
        sb.AppendLine("/nowarn:0618");
        // type defined in source files conficts with imported type at ref dll, using type in source file
        sb.AppendLine("/nowarn:0436");
        sb.AppendLine("/utf8output");
        sb.AppendLine("/preferreduilang:en-US");

        foreach (var src in _filesToCompile)
            sb.AppendLine($"\"{src}\"");

        File.WriteAllText(_rspPath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// 获取所有需要编译的文件，包括已改变的文件和可能的partial class所在的其它文件（只参与编译不会被hook）
    /// </summary>
    /// <remarks>需要像生成泛型pair一样生成返回所有hook pair的方法，而不是让主程序自己反射去读取，因为反射无法获取方法所在文件</remarks>
    void GetAllFilesToCompile()
    {
        var fileChanged = GlobalConfig.Instance.filesToCompile[moduleName];
        var defines = GlobalConfig.Instance.defines;

        var parseOpt = new CSharpParseOptions(LanguageVersion.Default, DocumentationMode.None, SourceCodeKind.Regular, defines);
        _partialClassScanner = new PartialClassScanner(moduleName, fileChanged, parseOpt);
        _partialClassScanner.Scan();

        _filesToCompile.Clear();
        _filesToCompile.AddRange(fileChanged);
        _filesToCompile.AddRange(_partialClassScanner.allFilesNeeded);
        _filesToCompile.Add(s_CS_File_Path__Patch_Assembly_Attr__);
        _filesToCompile.Add(s_CS_File_Path__Methods_For_Patch_Wrapper__Gen__);
        _filesToCompile = new List<string>(_filesToCompile.Distinct());

        _syntaxTrees = new List<SyntaxTree>();
        foreach(var file in _filesToCompile)
        {
            if(!_partialClassScanner.syntaxTrees.TryGetValue(file, out var syntaxTree))
            {
                syntaxTree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), parseOpt);
            }
            _syntaxTrees.Add(syntaxTree);
        }
    }

    int RunDotnetCompileProcess()
    {
        var si = new ProcessStartInfo();
        si.FileName = GlobalConfig.Instance.dotnetPath;
        si.Arguments = $"exec \"{GlobalConfig.Instance.cscPath}\" /nostdlib /noconfig /shared \"@{_rspPath}\"";

        si.CreateNoWindow = false;
        si.UseShellExecute = false;
        si.WindowStyle = ProcessWindowStyle.Hidden;
        si.RedirectStandardOutput = true;
        si.RedirectStandardError = true;
        si.StandardOutputEncoding = Encoding.UTF8;
        si.StandardErrorEncoding = Encoding.UTF8;
        si.WorkingDirectory = Environment.CurrentDirectory;

        var process = new Process();
        process.StartInfo = si;
        process.OutputDataReceived += (sender, args) =>
        {
            if (args.Data != null)
            {
                if (args.Data.Contains("error "))
                    Debug.LogError(args.Data);
                else if(args.Data.Contains("warning "))
                    Debug.LogWarning(args.Data);
                else
                    Debug.Log(args.Data);
            }
        };
        process.ErrorDataReceived += (sender, args) =>
        {
            if (args.Data != null)
                Debug.LogError(args.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit(kMaxCompileTime);

        return process.ExitCode;
    }

    /// <summary>
    /// 使用Roslyn编译dll（比csc.exe有更精细的控制权）, 但速度非常慢。。。
    /// </summary>
    /// <returns></returns>
    bool CompilePatchDllWithRoslyn()
    {
        string patchModuleName = Path.GetFileNameWithoutExtension(outputPath);
        string pdbPath = Path.ChangeExtension(outputPath, ".pdb");

        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithModuleName(patchModuleName)
            .WithAllowUnsafe(true)
            .WithDeterministic(true)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>()
            {
                { "CS0169",  ReportDiagnostic.Suppress },
                { "CS0649",  ReportDiagnostic.Suppress },
                { "CS1701",  ReportDiagnostic.Suppress },
                { "CS1702",  ReportDiagnostic.Suppress },
                { "CS0618",  ReportDiagnostic.Suppress },
                { "CS0436",  ReportDiagnostic.Suppress },
            })
            .WithMetadataImportOptions(MetadataImportOptions.Internal); // Internal 就够了，带上 private 符号量略多

        var topLevelBinderFlagsProperty = typeof(CSharpCompilationOptions).GetProperty("TopLevelBinderFlags", BindingFlags.Instance | BindingFlags.NonPublic);
        topLevelBinderFlagsProperty.SetValue(compilationOptions, (uint)1 << 22); // IgnoreAccessibility

        var refs = new List<MetadataReference>();
        foreach (var @ref in GlobalConfig.Instance.assemblyPathes.Values)
            refs.Add(MetadataReference.CreateFromFile(@ref));

        var compilation = CSharpCompilation.Create(patchModuleName, _syntaxTrees, refs, compilationOptions);

        using var fsDll = File.OpenWrite(outputPath);
        using var fsPdb = File.OpenWrite(pdbPath);

        var emitOpt = new EmitOptions(false, DebugInformationFormat.PortablePdb, pdbPath)
            .WithRuntimeMetadataVersion("v4.0.30319");

        EmitResult result = compilation.Emit(fsDll, fsPdb, options: emitOpt);
        if(!result.Success)
        {
            foreach(var item in result.Diagnostics)
            {
                if (item.Severity == DiagnosticSeverity.Error)
                    Debug.LogError(item.ToString());
                else if(item.Severity == DiagnosticSeverity.Warning)
                    Debug.LogWarning(item.ToString());
                else
                    Debug.Log(item.ToString());
            }
        }
        return result.Success;
    }
}
