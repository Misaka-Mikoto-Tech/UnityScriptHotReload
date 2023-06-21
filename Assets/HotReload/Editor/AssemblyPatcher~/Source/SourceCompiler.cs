//#define FOR_NET6_0_OR_GREATER

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace AssemblyPatcher;
public class SourceCompiler
{
    const int kMaxCompileTime = 60 * 1000;

    public string moduleName { get; private set; }
    public string outputPath { get; private set; }

    private string _rspPath;
    private static string s_CS_File_Path__Patch_Assembly_Attr__;            // __Patch_Assembly_Attr__.cs
    private static string s_CS_File_Path__Patch_GenericInst_Wrapper__Gen__; // __Patch_GenericInst_Wrapper__Gen__.cs

    private HashSet<string> _filesToCompile = new HashSet<string>();

    static SourceCompiler()
    {
        s_CS_File_Path__Patch_Assembly_Attr__ = GlobalConfig.Instance.tempScriptDir + $"/__Patch_Assembly_Attr__.cs";
        s_CS_File_Path__Patch_GenericInst_Wrapper__Gen__ = GlobalConfig.Instance.tempScriptDir + $"/__Patch_GenericInst_Wrapper__Gen__.cs";

        GenCSFile__Patch_Assembly_Attr__();
        GenCSFile__Patch_GenericInst_Wrapper__Gen__();
    }
    
    public SourceCompiler(string moduleName)
    {
        this.moduleName = moduleName;
        outputPath = string.Format(GlobalConfig.Instance.patchDllPathFormat, this.moduleName, GlobalConfig.Instance.patchNo);
    }
    
    public int DoCompile()
    {
        try
        {
            // 偶尔会出现删除失败，但编译完成之后也许又无占用了，所以也许可以允许失败？
            File.Delete(outputPath);
            File.Delete(Path.ChangeExtension(outputPath, ".pdb"));
        }
        catch(Exception ex)
        {
            Console.WriteLine($"删除文件失败:{ex.Message}");
        }

        _rspPath = GlobalConfig.Instance.tempScriptDir + $"/__{moduleName}_Patch.rsp";

        GetAllFilesToCompile();
        GenRspFile();
        int retCode = RunDotnetCompileProcess();
        return retCode;
    }

    /// <summary>
    /// 创建文件 __Patch_Assembly_Attr__.cs
    /// </summary>
    static void GenCSFile__Patch_Assembly_Attr__()
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Diagnostics;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Security.Permissions;");
        sb.AppendLine();
        sb.AppendLine("[assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations | DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints | DebuggableAttribute.DebuggingModes.EnableEditAndContinue)]");

#if FOR_NET6_0_OR_GREATER
        // for .netcore or newer
        sb.AppendLine($"[assembly: IgnoresAccessChecksTo(\"{_moduleName}\")]");
#else
        // for .net framework
        sb.AppendLine("[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]");
#endif

        File.WriteAllText(s_CS_File_Path__Patch_Assembly_Attr__, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// 创建文件 __Patch_GenericInst_Wrapper__Gen__.cs
    /// </summary>
    static void GenCSFile__Patch_GenericInst_Wrapper__Gen__()
    {
        string text = 
@"using System;
using System.Collections.Generic;
using System.Reflection;

namespace ScriptHotReload
{
    /// <summary>
    /// 用于 AssemblyPatcher 生成泛型实例定义的 wrapper 类型
    /// </summary>
    public class __Patch_GenericInst_Wrapper__Gen__
    {
        /// <summary>
        /// 扫描 base dll 里所有的泛型实例，然后获取与之关联的 patch dll 内创建的 wrapper 函数
        /// </summary>
        /// <returns></returns>
        public static Dictionary<MethodBase, MethodBase> GetGenericInstMethodForPatch()
        {
            // 函数体会被 Assembly Patcher 替换
            throw new NotImplementedException();
        }
    }
}
";
        File.WriteAllText(s_CS_File_Path__Patch_GenericInst_Wrapper__Gen__, text, Encoding.UTF8);
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

        sb.AppendLine($"\"{s_CS_File_Path__Patch_Assembly_Attr__}\"");
        sb.AppendLine($"\"{s_CS_File_Path__Patch_GenericInst_Wrapper__Gen__}\"");
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
        foreach (var f in  fileChanged)
            _filesToCompile.Add(f);

        ModuleDefData moduleData = ModuleDefPool.GetModuleData(moduleName);
        foreach(var (_, data) in moduleData.types)
        {
            // 只存在于一个文件内的类型一定不是位于多个文件内的 Partial Class（同一个文件内多次定义的Partial Class不用管）
            if (data.pdbDocuments.Count <= 1)
                continue;

            bool isTargetType = false;
            foreach(var pdb in data.pdbDocuments)
            {
                if(fileChanged.Contains(pdb.Url))
                {
                    isTargetType = true;
                    break;
                }
            }
            // 只要类型分布的源码文件有一个位于已更改源码文件列表内，就把其它的源码文件都添加进来
            if (isTargetType)
            {
                foreach (var pdb in data.pdbDocuments)
                    _filesToCompile.Add(pdb.Url);
            }
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

    private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        throw new NotImplementedException();
    }
}
