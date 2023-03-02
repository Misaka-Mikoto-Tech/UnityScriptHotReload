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
    private string _rspPath;
    private string _assAttrPath;
    public int DoCompile()
    {
        _rspPath = InputArgs.Instance.tempScriptDir + "/__Patch__.rsp";
        _assAttrPath = InputArgs.Instance.tempScriptDir + "/__Patch__Attr.cs";

        GenAssemblyAttributesFile();
        GenRspFile();
        int retCode = RunDotnetCompileProcess();
        return retCode;
    }

    void GenAssemblyAttributesFile()
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Diagnostics;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Security.Permissions;");
        sb.AppendLine();
        sb.AppendLine("[assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations | DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints | DebuggableAttribute.DebuggingModes.EnableEditAndContinue)]");
        // for .net framework
        sb.AppendLine("[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]");
#if FOR_NET6_0_OR_GREATER
        // for .netcore or newer
        foreach (var @ref in InputArgs.Instance.userAssemblyPathes.Keys)
            sb.AppendLine($"[assembly: IgnoresAccessChecksTo(\"{@ref}\")]");
#endif

        File.WriteAllText(_assAttrPath, sb.ToString(), Encoding.UTF8);
    }

    void GenRspFile()
    {
        var sb = new StringBuilder();
        sb.AppendLine("-target:library");
        sb.AppendLine($"-out:\"{InputArgs.Instance.patchDllPath}\"");
        foreach(var def in InputArgs.Instance.defines)
            sb.AppendLine($"-define:{def}");
        foreach(var @ref in InputArgs.Instance.fallbackAssemblyPathes.Values)
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

        sb.AppendLine($"\"{_assAttrPath}\"");
        foreach(var src in InputArgs.Instance.filesChanged)
            sb.AppendLine($"\"{src}\"");

        File.WriteAllText(_rspPath, sb.ToString(), Encoding.UTF8);
    }

    int RunDotnetCompileProcess()
    {
        var si = new ProcessStartInfo();
        si.FileName = InputArgs.Instance.dotnetPath;
        si.Arguments = $"exec \"{InputArgs.Instance.cscPath}\" /nostdlib /noconfig /shared \"@{_rspPath}\"";

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
                if (args.Data.StartsWith("error"))
                    Debug.LogError(args.Data);
                else if(args.Data.StartsWith("warning"))
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
