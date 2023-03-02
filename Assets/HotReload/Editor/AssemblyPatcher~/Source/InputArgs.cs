using SimpleJSON;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssemblyPatcher;

[Serializable]
public class InputArgs
{
    public static InputArgs Instance;

    public int patchNo;
    public string workDir;
    public string dotnetPath;
    public string cscPath;
    
    public string tempScriptDir;
    public string builtinAssembliesDir;
    public string patchDllPath;
    public string lambdaWrapperBackend;

    public string[] filesChanged;

    public string[] defines;
    public Dictionary<string, string> fallbackAssemblyPathes;
    public Dictionary<string, string> userAssemblyPathes; // 非系统和Unity相关的用户自己的dll
    public HashSet<string> searchPaths;

    [NonSerialized]
    public string patchDllSuffix;

    public static void LoadFromFile(string inputFilePath)
    {
        JSONNode root = JSON.Parse(File.ReadAllText(inputFilePath, Encoding.UTF8));
        InputArgs args = new InputArgs();
        args.patchNo = root["patchNo"];
        args.workDir = root["workDir"];
        args.dotnetPath = root["dotnetPath"];
        args.cscPath = root["cscPath"];
        args.filesChanged = root["filesChanged"];
        args.tempScriptDir = root["tempScriptDir"];
        args.builtinAssembliesDir = root["builtinAssembliesDir"];
        args.patchDllPath = root["patchDllPath"];
        args.lambdaWrapperBackend = root["lambdaWrapperBackend"];

        args.defines = root["defines"];
        string[] fallbackAsses = root["fallbackAssemblyPathes"];
        args.fallbackAssemblyPathes = new Dictionary<string, string>();
        args.userAssemblyPathes = new Dictionary<string, string>();
        args.searchPaths = new HashSet<string>();

        foreach (string ass in fallbackAsses)
        {
            string fileNameNoExt = Path.GetFileNameWithoutExtension(ass);
            args.fallbackAssemblyPathes.TryAdd(fileNameNoExt, ass);

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
