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
    string _inputFilePath;
    string _outputFilePath;
    InputArgs _inputArgs;
    Dictionary<string, List<MethodData>> _methodsNeedHook = new Dictionary<string, List<MethodData>>();

    ModuleContext _ctxBase, _ctxNew;

    public Patcher(string inputFilePath, string outputFilePath)
    {
        _inputFilePath = inputFilePath;
        _outputFilePath = outputFilePath;
        ParseInputJsonStr();
        _inputArgs = InputArgs.Instance;
        Environment.CurrentDirectory = _inputArgs.workDir;

        // 提前把相关dll都载入，方便查找对应类型
        Stopwatch sw = new Stopwatch();
        sw.Start();
        foreach (var kv in _inputArgs.fallbackAssemblyPathes)
        {
            try
            {
                Assembly.LoadFrom(kv.Value);
            }
            catch(Exception ex)
            {
                Debug.LogError($"load dll fail:{kv.Value}\r\n:{ex.Message}\r\n{ex.StackTrace}");
            }
        }
        sw.Stop();
        Console.WriteLine($"[Debug]载入相关dll耗时 {sw.ElapsedMilliseconds} ms");

        CreateCtx();
    }

    void CreateCtx()
    {
        {
            var baseResolver = new dnlib.DotNet.AssemblyResolver();
            _ctxBase = new ModuleContext(baseResolver, null);
            baseResolver.DefaultModuleContext = _ctxBase;

            foreach (var ass in _inputArgs.searchPaths)
                baseResolver.PostSearchPaths.Add(ass);
            baseResolver.PostSearchPaths.Add(_inputArgs.builtinAssembliesDir);
        }

        {
            var newResolver = new dnlib.DotNet.AssemblyResolver();
            _ctxNew = new ModuleContext(newResolver, null);
            newResolver.DefaultModuleContext = _ctxNew;

            foreach (var ass in _inputArgs.searchPaths)
                newResolver.PostSearchPaths.Add(ass);
            newResolver.PostSearchPaths.Add(_inputArgs.tempCompileToDir);
        }
    }

    public bool DoPatch()
    {
        //File.Delete(_outputFilePath);

        foreach (string assName in _inputArgs.assembliesToPatch)
        {
            string assNameNoExt = Path.GetFileNameWithoutExtension(assName);

            string baseDll = $"{_inputArgs.builtinAssembliesDir}/{assName}";
            string lastDll = string.Format(_inputArgs.lastDllPathFmt, assNameNoExt);
            string newDll = $"{_inputArgs.tempCompileToDir}/{assName}";

            if(!File.Exists(baseDll))
            {
                Debug.LogError($"file `{baseDll}` does not exists");
                continue;
            }

            if (IsFilesEqual(newDll, lastDll))
                continue;

            using (var baseAssDef = ModuleDefMD.Load(baseDll, _ctxBase))
            {
                using (var newAssDef = ModuleDefMD.Load(newDll, _ctxNew))
                {
                    var assBuilder = new AssemblyDataBuilder(baseAssDef, newAssDef);
                    if (!assBuilder.DoBuild(_inputArgs.patchNo))
                    {
                        Debug.LogError($"[Error][{assName}]不符合热重载条件，停止重载");
                        return false;
                    }

                    string patchDll = string.Format(_inputArgs.patchDllPathFmt, assNameNoExt, _inputArgs.patchNo);
                    newAssDef.Write(patchDll);

                    // 有可能数量为0，此时虽然与原始dll无差异，但是与上一次编译有差异，也需要处理（清除已hook函数）
                    _methodsNeedHook.Add(assName, (from data in assBuilder.assemblyData.methodsNeedHook.Values select data.baseMethod).ToList());

                    File.Copy(newDll, lastDll, true);
                }
            }
        }

        GenOutputJsonFile();

        return true;
    }

    private void ParseInputJsonStr()
    {
        JSONNode root = JSON.Parse(File.ReadAllText(_inputFilePath));
        InputArgs args = new InputArgs();
        args.patchNo = root["patchNo"];
        args.workDir = root["workDir"];
        args.assembliesToPatch = root["assembliesToPatch"];
        args.patchAssemblyNameFmt = root["patchAssemblyNameFmt"];
        args.tempScriptDir = root["tempScriptDir"];
        args.tempCompileToDir = root["tempCompileToDir"];
        args.builtinAssembliesDir = root["builtinAssembliesDir"];
        args.lastDllPathFmt = root["lastDllPathFmt"];
        args.patchDllPathFmt = root["patchDllPathFmt"];
        args.lambdaWrapperBackend = root["lambdaWrapperBackend"];

        string[] fallbackAsses = root["fallbackAssemblyPathes"];
        args.fallbackAssemblyPathes = new Dictionary<string, string>();
        args.searchPaths = new HashSet<string>();
        foreach(string ass in fallbackAsses)
        {
            var ass2 = ass.Replace('\\', '/');
            args.fallbackAssemblyPathes.TryAdd(Path.GetFileNameWithoutExtension(ass2), ass2);

            if(!ass2.Contains("Library/ScriptAssemblies"))
                args.searchPaths.Add(Path.GetDirectoryName(ass2));
        }

        InputArgs.Instance = args;
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
        root.Add("patchNo", InputArgs.Instance.patchNo);
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
