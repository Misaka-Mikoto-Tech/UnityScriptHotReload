using SimpleJSON;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssemblyPatcher;

public class GlobalConfig
{
    public static GlobalConfig Instance;

    public int patchNo;
    public string workDir;
    public string dotnetPath;
    public string cscPath;
    
    public string tempScriptDir;
    public string builtinAssembliesDir;
    public string patchDllPathFormat;
    public string lambdaWrapperBackend;

    /// <summary>
    /// 需要编译的文件, key: AssemblyName, value: FileList
    /// </summary>
    public Dictionary<string, List<string>> filesToCompile;

    public string[] defines;
    public Dictionary<string, string> assemblyPathes; // (name, path)
    public Dictionary<string, string> userAssemblyPathes; // 非系统和Unity相关的用户自己的dll
    public HashSet<string> searchPaths;


    public static void LoadFromFile(string inputFilePath)
    {
        JSONNode root = JSON.Parse(File.ReadAllText(inputFilePath, Encoding.UTF8));
        GlobalConfig args = new GlobalConfig();
        args.patchNo = root["patchNo"];
        args.workDir = root["workDir"];
        args.dotnetPath = root["dotnetPath"];
        args.cscPath = root["cscPath"];
        args.tempScriptDir = root["tempScriptDir"];
        args.builtinAssembliesDir = root["builtinAssembliesDir"];
        args.patchDllPathFormat = root["patchDllPathFormat"];
        args.lambdaWrapperBackend = root["lambdaWrapperBackend"];

        args.defines = root["defines"];
        string[] allAsses = root["allAssemblyPathes"];
        args.assemblyPathes = new Dictionary<string, string>();
        args.userAssemblyPathes = new Dictionary<string, string>();
        args.searchPaths = new HashSet<string>();

        args.filesToCompile = new Dictionary<string, List<string>>();
        foreach(var str in (string[])root["filesChanged"])
        {
            string[] kv = str.Split(':');
            string filePath = kv[0];
            string assemblyName = kv[1];
            if(!args.filesToCompile.TryGetValue(assemblyName, out var files))
            {
                files = new List<string>();
                args.filesToCompile.Add(assemblyName, files);
            }
            files.Add(filePath);
        }

        foreach (string ass in allAsses)
        {
            string fileNameNoExt = Path.GetFileNameWithoutExtension(ass);
            args.assemblyPathes.TryAdd(fileNameNoExt, ass);

            if (!ass.Contains("Library/ScriptAssemblies"))
                args.searchPaths.Add(Path.GetDirectoryName(ass));

            // 默认认为用户不会修改unity官方代码, 可根据自己需求自行调整
            if(ass.StartsWith(args.workDir) && !ass.Contains("/com.unity."))
            {
                if (!fileNameNoExt.StartsWith("Unity")
                    && !fileNameNoExt.StartsWith("System."))
                        args.userAssemblyPathes.Add(fileNameNoExt, ass);
            }
        }

        Instance = args;
    }
}
