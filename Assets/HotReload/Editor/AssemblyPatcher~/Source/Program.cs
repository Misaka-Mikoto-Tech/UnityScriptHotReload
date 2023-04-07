using NHibernate.Util;
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
                foreach (var moduleName in GlobalConfig.Instance.filesToCompile.Keys)
                    patcherTasks.Add(Task.Run(new AssemblyPatcher(moduleName).DoPatch));

                foreach (var task in patcherTasks)
                {
                    task.Wait();
                    if (!task.Result)
                        goto Fail;
                }

                success = true;
                break;
            Fail:;
            } while (false);
        }
        catch(Exception ex)
        {
            Debug.LogError($"Patch Fail:{ex.Message}, stack:{ex.StackTrace}");
        }
        sw.Stop();
        Debug.LogDebug($"执行Patch耗时 {sw.ElapsedMilliseconds} ms");
        if (!success)
            return -1;

        Debug.Log("Patch成功");
        return 0;
    }
}