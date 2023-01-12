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
    public string[] assembliesToPatch;
    public string patchAssemblyNameFmt;
    public string tempScriptDir;
    public string tempCompileToDir;
    public string builtinAssembliesDir;
    public string lastDllPathFmt;
    public string patchDllPathFmt;
    public string lambdaWrapperBackend;

    public Dictionary<string, string> fallbackAssemblyPathes;

    [NonSerialized]
    public string patchDllSuffix;
}
