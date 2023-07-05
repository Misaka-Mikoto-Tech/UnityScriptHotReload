/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */

using SimpleJSON;
using System.Diagnostics;
using System.Text;

namespace AssemblyPatcher;

internal class Program
{
    static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        if(args.Length < 2)
        {
            Debug.Log("usage: AssemblyPatcher <InputFile.json> <OutputFile.json> [debug]");
            return -1;
        }

        if (!File.Exists(args[0]))
        {
            Debug.Log($"file `{args[0]}` does not exists");
            return -1;
        }

        if (args.Length > 2 && args[2] == "debug")
        {
            Debug.Log("waitting for debugger attach");
            System.Diagnostics.Debugger.Launch();
        }

        MonkeyHooks.Init();

        Stopwatch sw = new Stopwatch();
        sw.Start();
        bool success = false;
        try
        {
            do
            {
                GlobalConfig.LoadFromFile(args[0]);
                Environment.CurrentDirectory = GlobalConfig.Instance.workDir;
                
                // 多线程编译
                var compileTasks = new Dictionary<SourceCompiler, Task<int>>();
                foreach (var moduleName in GlobalConfig.Instance.filesToCompile.Keys)
                {
                    var compiler = new SourceCompiler(moduleName);
                    compileTasks.Add(compiler, Task.Run(compiler.DoCompile));
                }

                foreach(var (compiler, task) in compileTasks)
                {
                    task.Wait();
                    if (task.Result != 0)
                        goto Fail;

                    GlobalConfig.Instance.assemblyPathes.Add(Path.GetFileNameWithoutExtension(compiler.outputPath), compiler.outputPath);
                }

                // 多线程 Patch
                var patcherTasks = new List<Task<bool>>();
                var patchers = new List<AssemblyPatcher>();
                foreach (var moduleName in GlobalConfig.Instance.filesToCompile.Keys)
                {
                    var patcher = new AssemblyPatcher(moduleName);
                    patchers.Add(patcher);
                    patcherTasks.Add(Task.Run(patcher.DoPatch));
                }

                foreach (var task in patcherTasks)
                {
                    task.Wait();
                    if (!task.Result)
                        goto Fail;
                }

                // 生成输出配置文件
                var methodsNeedHook = new Dictionary<string, List<MethodData>>();
                foreach (var patcher in patchers)
                {
                    methodsNeedHook.Add(patcher.moduleName, new List<MethodData>(patcher.assemblyDataForPatch.methodsNeedHook));
                }
                GenOutputJsonFile(args[1], methodsNeedHook);

                // 将 patch dll 写入文件
                foreach (var patcher in patchers)
                {
                    patcher.WriteToFile();
                }

                success = true;
                break;
            Fail:;
            } while (false);
        }
        catch(Exception ex)
        {
            var msg = ex.Message;
            var stackTrace = ex.StackTrace;
            if (ex.InnerException != null)
            {
                msg += ex.InnerException.Message;
                stackTrace = ex.InnerException.StackTrace + ex.StackTrace;
            }
            Debug.LogError($"Patch Fail:{msg}, stack:{stackTrace}");
        }
        sw.Stop();
        Debug.LogDebug($"总耗时 {sw.ElapsedMilliseconds} ms");
        if (!success)
            return -1;

        Debug.Log("Patch成功");
        return 0;
    }

    static void GenOutputJsonFile(string outputPath, Dictionary<string, List<MethodData>> methodsNeedHook)
    {
        /*
         * {
         *   "patchNo" : 0,
         *   "assemblyChangedFromLast" : [
         *   "TestDllA"
         *   ],
         *   "methodsNeedHook":[
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
        foreach (var (ass, methods) in methodsNeedHook)
        {
            assChanged.Add(ass);
            for (int i = 0, imax = methods.Count; i < imax; i++)
            {
                // document 为 null 方法没有可见代码，比如 event 字段生成的方法，没有hook的必要
                if (methods[i].document != null)
                {
                    arrMethodsNeedHook[idx++] = methods[i].ToJsonNode();
                }
            }
        }

        StringBuilder sb = new StringBuilder();
        root.WriteToStringBuilder(sb, 0, 4, JSONTextMode.Indent);
        File.WriteAllText(outputPath, sb.ToString());
    }
}