using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Permissions;
using System.Text;
using System.Threading.Tasks;
using SimpleJSON;
using dnlib;
using dnlib.DotNet;
using static AssemblyPatcher.Utils;

namespace AssemblyPatcher;

public class Patcher
{
    string _outputFilePath;
    GlobalConfig _inputArgs;
    Dictionary<string, List<MethodData>> _methodsNeedHook = new Dictionary<string, List<MethodData>>();

    public Patcher(string moduleName, string outputFilePath)
    {
        _outputFilePath = outputFilePath;
        _inputArgs = GlobalConfig.Instance;
        Environment.CurrentDirectory = _inputArgs.workDir;
    }

    public bool DoPatch()
    {
        //File.Delete(_outputFilePath);

        foreach(var kv in GlobalConfig.Instance.filesToCompile)
        {

        }

        //foreach (string assName in _inputArgs.assembliesToPatch)
        //{
        //    string assNameNoExt = Path.GetFileNameWithoutExtension(assName);

        //    string baseDll = $"{_inputArgs.builtinAssembliesDir}/{assName}";
        //    string lastDll = string.Format(_inputArgs.lastDllPathFmt, assNameNoExt);
        //    string newDll = $"{_inputArgs.tempCompileToDir}/{assName}";

        //    if(!File.Exists(baseDll))
        //    {
        //        Debug.LogError($"file `{baseDll}` does not exists");
        //        continue;
        //    }

        //    if (IsFilesEqual(newDll, lastDll))
        //        continue;

        //    using (var baseAssDef = ModuleDefMD.Load(baseDll, _ctxBase))
        //    {
        //        using (var newAssDef = ModuleDefMD.Load(newDll, _ctxNew))
        //        {
        //            var assBuilder = new AssemblyDataBuilder(baseAssDef, newAssDef);
        //            if (!assBuilder.DoBuild(_inputArgs.patchNo))
        //            {
        //                Debug.LogError($"[Error][{assName}]不符合热重载条件，停止重载");
        //                return false;
        //            }

        //            string patchDll = string.Format(_inputArgs.patchDllPath, assNameNoExt, _inputArgs.patchNo);
        //            newAssDef.Write(patchDll);

        //            // 有可能数量为0，此时虽然与原始dll无差异，但是与上一次编译有差异，也需要处理（清除已hook函数）
        //            _methodsNeedHook.Add(assName, (from data in assBuilder.assemblyData.methodsNeedHook.Values select data.baseMethod).ToList());

        //            File.Copy(newDll, lastDll, true);
        //        }
        //    }
        //}

        GenOutputJsonFile();

        return true;
    }

    private void GenOutputJsonFile()
    {
        /*
         * {
         *   "modifiedMethods":[
         *       {"name":"FuncA", "type":"", "isConstructor": true ...},
         *       {"name":"FuncB", "type":"", "isConstructor": false ...},
         *       ...
         *     ]
         * }
         */

        JSONObject root = new JSONObject();
        root.Add("patchNo", GlobalConfig.Instance.patchNo);
        JSONArray assChanged = new JSONArray();
        root.Add("assemblyChangedFromLast", assChanged);
        JSONArray arrMethodsNeedHook = new JSONArray();
        root.Add("methodsNeedHook", arrMethodsNeedHook);
        int idx = 0;
        foreach(var kv in _methodsNeedHook)
        {
            assChanged.Add(kv.Key);
            for(int i = 0, imax = kv.Value.Count; i < imax; i++)
            {
                arrMethodsNeedHook[idx++] = kv.Value[i].ToJsonNode();
            }
        }

        StringBuilder sb = new StringBuilder();
        root.WriteToStringBuilder(sb, 0, 4, JSONTextMode.Indent);
        File.WriteAllText(_outputFilePath, sb.ToString());
    }

}
