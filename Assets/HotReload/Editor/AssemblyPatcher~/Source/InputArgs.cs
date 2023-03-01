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
        args.searchPaths = new HashSet<string>();
        foreach (string ass in fallbackAsses)
        {
            args.fallbackAssemblyPathes.TryAdd(Path.GetFileNameWithoutExtension(ass), ass);

            if (!ass.Contains("Library/ScriptAssemblies"))
                args.searchPaths.Add(Path.GetDirectoryName(ass));
        }

        Instance = args;
    }
}
