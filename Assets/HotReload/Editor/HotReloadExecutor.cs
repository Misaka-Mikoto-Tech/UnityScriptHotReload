using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Reflection;

using static ScriptHotReload.HotReloadUtils;
using static ScriptHotReload.HotReloadConfig;
using System.Text;

namespace ScriptHotReload
{
    [InitializeOnLoad]
    public class HotReloadExecutor
    {
        public static int patchNo { get; private set; } = 0;
        static string _dotnetPath;
        static string _cscPath;

        static Task<int> _patchTask;
        static ConcurrentQueue<string> _patchTaskOutput = new ConcurrentQueue<string>();
        static List<MethodBase> _methodsToHook = new List<MethodBase>();

        static HotReloadExecutor()
        {
            patchNo = 0;

            string dotnetName = "dotnet";
            string cscName = "csc.dll";
#if UNITY_EDITOR_WIN
            dotnetName += ".exe";
#endif
            var unityEditorPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            _dotnetPath = Directory.GetFiles(unityEditorPath, dotnetName, SearchOption.AllDirectories).FirstOrDefault().Replace('\\', '/');
            _cscPath = Directory.GetFiles(unityEditorPath, cscName, SearchOption.AllDirectories).FirstOrDefault().Replace('\\', '/');

            UnityEngine.Debug.Assert(!string.IsNullOrEmpty(_dotnetPath));
            UnityEngine.Debug.Assert(!string.IsNullOrEmpty(_cscPath));

            EditorApplication.update += OnEditorUpdate;
        }

        private static void OnEditorUpdate()
        {
            if (!hotReloadEnabled) return;
            if (_patchTask != null)
            {
                DispatchTaskOutput();
                if (_patchTask.IsCompleted)
                {
                    if(_patchTask.Result == 0)
                    {
                        //HookAssemblies.DoHook(_methodsToHook);
                        if (_methodsToHook.Count > 0)
                            patchNo++;

                        UnityEngine.Debug.Log("<color=yellow>热重载完成</color>");
                    }
                    _patchTask = null;
                }
                return;
            }

            if (!FileWatcher.changedSinceLastGet
                || new TimeSpan(DateTime.Now.Ticks - FileWatcher.lastModifyTime.Ticks).TotalSeconds < kFileChangeCheckSpan)
                return;

            GenPatcherInputArgsFile();
            //_patchTask = Task.Run(RunAssemblyPatchProcess);
        }

        [Serializable]
        public class InputArgs
        {
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
            public string[] fallbackAssemblyPathes;
        }
        static void GenPatcherInputArgsFile()
        {
            var inputArgs = new InputArgs();
            inputArgs.patchNo = patchNo;
            inputArgs.workDir = Environment.CurrentDirectory.Replace('\\', '/');
            inputArgs.dotnetPath = _dotnetPath;
            inputArgs.cscPath = _cscPath;
            
            inputArgs.tempScriptDir = $"{inputArgs.workDir}/{kTempScriptDir}";
            inputArgs.builtinAssembliesDir = $"{inputArgs.workDir}/{kBuiltinAssembliesDir}";
            inputArgs.patchDllPath = $"{inputArgs.workDir}/{string.Format(kPatchDllPathFormat, patchNo)}";
            inputArgs.lambdaWrapperBackend = kLambdaWrapperBackend;

            inputArgs.filesChanged = FileWatcher.GetChangedFile();

            inputArgs.defines = EditorUserBuildSettings.activeScriptCompilationDefines;
            inputArgs.fallbackAssemblyPathes = GetFallbackAssemblyPaths();

            string jsonStr = JsonUtility.ToJson(inputArgs, true);
            File.WriteAllText(kAssemblyPatcherInput, jsonStr, Encoding.UTF8);
        }

        private static int RunAssemblyPatchProcess()
        {
            var startInfo = new ProcessStartInfo();
            startInfo.FileName = Path.GetDirectoryName(GetThisFilePath()) + "/AssemblyPatcher~/AssemblyPatcher.exe";
#if PATCHER_DEBUG
            startInfo.Arguments = $"{kAssemblyPatcherInput} {kAssemblyPatcherOutput} debug";
            startInfo.CreateNoWindow = false;
#else
            startInfo.Arguments = $"{kAssemblyPatcherInput} {kAssemblyPatcherOutput}";
            startInfo.CreateNoWindow = true;
#endif
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            //startInfo.StandardInputEncoding = System.Text.UTF8Encoding.UTF8;
            startInfo.StandardOutputEncoding = System.Text.UTF8Encoding.UTF8;
            startInfo.StandardErrorEncoding = System.Text.UTF8Encoding.UTF8;
            Process procPathcer = new Process();
            procPathcer.StartInfo = startInfo;
            procPathcer.Start();

            Action<StreamReader> outputProcMsgs = sr =>
            {
                string line = sr.ReadLine();
                while (line != null)
                {
                    _patchTaskOutput.Enqueue(line);
                    line = sr.ReadLine();
                }
            };

            using (var sr = procPathcer.StandardOutput) { outputProcMsgs(sr); }
            using (var sr = procPathcer.StandardError) { outputProcMsgs(sr); }

            int exitCode = -1;
            if (procPathcer.WaitForExit(60 * 1000)) // 最长等待1分钟
                exitCode = procPathcer.ExitCode;
            else
                procPathcer.Kill();

            if (exitCode == 0)
                ParseOutputReport();

            return exitCode;
        }

        static void DispatchTaskOutput()
        {
            while(_patchTaskOutput.TryDequeue(out var line))
            {
                line = line.Replace("<br/>", "\r\n");
                if (line.StartsWith("[Info]"))
                    UnityEngine.Debug.Log($"<color=lime>[Patcher] {line.Substring("[Info]".Length)}</color>");
                else if (line.StartsWith("[Warning]"))
                    UnityEngine.Debug.LogWarning($"<color=orange>[Patcher] {line.Substring("[Warning]".Length)}</color>");
                else if (line.StartsWith("[Error]"))
                    UnityEngine.Debug.LogError($"[Patcher] {line.Substring("[Error]".Length)}");

#if PATCHER_DEBUG || true
                else if (line.StartsWith("[Debug]"))
                    UnityEngine.Debug.Log($"<color=yellow>[Patcher] {line.Substring("[Debug]".Length)}</color>");
#endif
                else
                    UnityEngine.Debug.Log($"<color=white>[Patcher] {line}</color>");
            }
        }

        [Serializable]
        public class OutputReport
        {
            [Serializable]
            public class MethodData
            {
                public string name;
                public string type;
                public bool isConstructor;
                public bool isGeneric;
                public bool isPublic;
                public bool isStatic;
                public bool isLambda;
                public bool ilChanged;
                public string document;
                public string returnType;
                public string[] paramTypes;
            }
            public int patchNo;
            public List<MethodData> methodsNeedHook;
        }

        static void ParseOutputReport()
        {
            _methodsToHook.Clear();

            if (!File.Exists(kAssemblyPatcherOutput))
                throw new Exception($"can not find output report file `{kAssemblyPatcherOutput}`");

            string text = File.ReadAllText(kAssemblyPatcherOutput);
            var outputReport = JsonUtility.FromJson<OutputReport>(text);

            foreach (var data in outputReport.methodsNeedHook)
            {
                if (data.isGeneric)
                    continue;

                Type t = Type.GetType(data.type, true);

                BindingFlags flags = BindingFlags.Default;
                flags |= data.isPublic ? BindingFlags.Public : BindingFlags.NonPublic;
                flags |= data.isStatic ? BindingFlags.Static : BindingFlags.Instance;

                Type[] paramTypes = new Type[data.paramTypes.Length];
                for (int i = 0, imax = paramTypes.Length; i < imax; i++)
                {
                    paramTypes[i] = Type.GetType(data.paramTypes[i], true);
                }
                MethodBase method;
                if (data.isConstructor)
                    method = t.GetConstructor(flags, null, paramTypes, null);
                else
                    method = t.GetMethod(data.name, flags, null, paramTypes, null);

                if (method == null)
                    throw new Exception($"can not find method `{data.name}`");

                _methodsToHook.Add(method);
            }
        }
    }

}
