///*
// * Author: Misaka Mikoto
// * email: easy66@live.com
// * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
// */

////#define PATCHER_DEBUG

//using System.Collections;
//using System.Collections.Generic;
//using UnityEngine;
//using UnityEditor;
//using static UnityEngine.GraphicsBuffer;
//using UnityEditor.Build.Player;
//using System.IO;
//using UnityEditor.Callbacks;
//using System.Reflection;
//using MonoHook;
//using System.Runtime.CompilerServices;
//using System;
//using System.Reflection.Emit;
//using System.Linq;
//using System.Text;

//using static ScriptHotReload.HotReloadUtils;
//using static ScriptHotReload.HotReloadConfig;
//using System.Diagnostics;

//namespace ScriptHotReload
//{
//    public class GenPatchAssemblies
//    {
//        public static bool codeHasChanged => methodsToHook.Count > 0;

//        public static Dictionary<string, List<MethodBase>> methodsToHook { get; private set; } = new Dictionary<string, List<MethodBase>>();

//        public static int patchNo { get; private set; } = 0;
//        static string _dotnetPath;
//        static string _cscPath;

//        [InitializeOnLoadMethod]
//        static void Init()
//        {
//            patchNo = 0;
//            methodsToHook.Clear();

//            string dotnetName = "dotnet";
//            string cscName = "csc.dll";
//#if UNITY_EDITOR_WIN
//            dotnetName += ".exe";
//#endif
//            var unityEditorPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
//            _dotnetPath = Directory.GetFiles(unityEditorPath, dotnetName, SearchOption.AllDirectories).FirstOrDefault();
//            _cscPath = Directory.GetFiles(unityEditorPath, cscName, SearchOption.AllDirectories).FirstOrDefault();

//            UnityEngine.Debug.Assert(!string.IsNullOrEmpty(_dotnetPath));
//            UnityEngine.Debug.Assert(!string.IsNullOrEmpty(_cscPath));
//        }

//        [MenuItem("ScriptHotReload/PatchAssemblies")]
//        public static void DoGenPatchAssemblies()
//        {
//            if (!Application.isPlaying)
//                return;

//            CompileScript.OnCompileSuccess = OnScriptCompileSuccess;
//            CompileScript.CompileScriptToDir(kTempCompileToDir);
//        }

//        public static void OnScriptCompileSuccess(CompileStatus status)
//        {
//            if (status != CompileStatus.Idle)
//                return;

//            methodsToHook.Clear();
//            GenPatcherInputArgsFile();
//            if (RunAssemblyPatchProcess() != 0)
//                return;

//            ParseOutputReport();
//            if (methodsToHook.Count == 0)
//            {
//                UnityEngine.Debug.Log("代码没有发生改变，不执行热重载");
//                return;
//            }

//            HookAssemblies.DoHook(methodsToHook);
//            if (methodsToHook.Count > 0)
//                patchNo++;

//            UnityEngine.Debug.Log("<color=yellow>热重载完成</color>");
//        }

//        private static int RunAssemblyPatchProcess()
//        {
//            var startInfo = new ProcessStartInfo();
//            startInfo.FileName = Path.GetDirectoryName(GetThisFilePath()) + "/AssemblyPatcher~/AssemblyPatcher.exe";
//#if PATCHER_DEBUG
//            startInfo.Arguments = $"{kAssemblyPatcherInput} {kAssemblyPatcherOutput} debug";
//            startInfo.CreateNoWindow = false;
//#else
//            startInfo.Arguments = $"{kAssemblyPatcherInput} {kAssemblyPatcherOutput}";
//            startInfo.CreateNoWindow = true;
//#endif
//            startInfo.UseShellExecute = false;
//            startInfo.RedirectStandardOutput = true;
//            startInfo.RedirectStandardError = true;
//            //startInfo.StandardInputEncoding = System.Text.UTF8Encoding.UTF8;
//            startInfo.StandardOutputEncoding = System.Text.UTF8Encoding.UTF8;
//            startInfo.StandardErrorEncoding = System.Text.UTF8Encoding.UTF8;
//            Process procPathcer = new Process();
//            procPathcer.StartInfo = startInfo;
//            procPathcer.Start();

//            Action<StreamReader> outputProcMsgs = sr =>
//            {
//                string line = sr.ReadLine();
//                while (line != null)
//                {
//                    line = line.Replace("<br/>", "\r\n");
//                    if (line.StartsWith("[Info]"))
//                        UnityEngine.Debug.Log($"<color=lime>[Patcher] {line.Substring("[Info]".Length)}</color>");
//                    else if (line.StartsWith("[Warning]"))
//                        UnityEngine.Debug.LogWarning($"<color=orange>[Patcher] {line.Substring("[Warning]".Length)}</color>");
//                    else if (line.StartsWith("[Error]"))
//                        UnityEngine.Debug.LogError($"[Patcher] {line.Substring("[Error]".Length)}");

//#if PATCHER_DEBUG || true
//                    else if (line.StartsWith("[Debug]"))
//                        UnityEngine.Debug.Log($"<color=yellow>[Patcher] {line.Substring("[Debug]".Length)}</color>");
//#endif
//                    else
//                        UnityEngine.Debug.Log($"<color=white>[Patcher] {line}</color>");

//                    line = sr.ReadLine();
//                }
//            };

//            using (var sr = procPathcer.StandardOutput) { outputProcMsgs(sr); }
//            using (var sr = procPathcer.StandardError) { outputProcMsgs(sr); }

//            int exitCode = -1;
//            if (procPathcer.WaitForExit(60 * 1000)) // 最长等待1分钟
//                exitCode = procPathcer.ExitCode;
//            else
//                procPathcer.Kill();

//            return exitCode;
//        }

//        [Serializable]
//        public class InputArgs
//        {
//            public int patchNo;
//            public string workDir;
//            public string dotnetPath;
//            public string cscPath;
//            public string[] filesChanged;
//            public string[] assembliesToPatch;
//            public string patchAssemblyNameFmt;
//            public string tempScriptDir;
//            public string tempCompileToDir;
//            public string builtinAssembliesDir;
//            public string lastDllPathFmt;
//            public string patchDllPathFmt;
//            public string lambdaWrapperBackend;

//            public string[] defines;
//            public string[] fallbackAssemblyPathes;
//        }

//        static void GenPatcherInputArgsFile()
//        {
//            var inputArgs = new InputArgs();
//            inputArgs.patchNo = patchNo;
//            inputArgs.workDir = Environment.CurrentDirectory;
//            inputArgs.dotnetPath = _dotnetPath;
//            inputArgs.cscPath = _cscPath;
//            inputArgs.assembliesToPatch = hotReloadAssemblies.ToArray();
//            inputArgs.patchAssemblyNameFmt = kPatchAssemblyName;
//            inputArgs.tempScriptDir = kTempScriptDir;
//            inputArgs.tempCompileToDir = kTempCompileToDir;
//            inputArgs.builtinAssembliesDir = kBuiltinAssembliesDir;
//            inputArgs.lastDllPathFmt = kLastDllPathFormat;
//            inputArgs.patchDllPathFmt = kPatchDllPathFormat;
//            inputArgs.lambdaWrapperBackend = kLambdaWrapperBackend;
//            inputArgs.fallbackAssemblyPathes = GetFallbackAssemblyPaths().Values.ToArray();

//            string jsonStr = JsonUtility.ToJson(inputArgs, true);
//            File.WriteAllText(kAssemblyPatcherInput, jsonStr);
//        }

//        [Serializable]
//        public class OutputReport
//        {
//            [Serializable]
//            public class MethodData
//            {
//                public string name;
//                public string type;
//                public string assembly;
//                public bool isConstructor;
//                public bool isGeneric;
//                public bool isPublic;
//                public bool isStatic;
//                public bool isLambda;
//                public bool ilChanged;
//                public string document;
//                public string returnType;
//                public string[] paramTypes;
//            }
//            public int patchNo;
//            public string[] assemblyChangedFromLast;
//            public List<MethodData> methodsNeedHook;
//        }

//        static void ParseOutputReport()
//        {
//            methodsToHook.Clear();

//            if (!File.Exists(kAssemblyPatcherOutput))
//                throw new Exception($"can not find output report file `{kAssemblyPatcherOutput}`");

//            string text = File.ReadAllText(kAssemblyPatcherOutput);
//            var outputReport = JsonUtility.FromJson<OutputReport>(text);

//            foreach(var assName in outputReport.assemblyChangedFromLast)
//            {
//                methodsToHook.Add(assName, new List<MethodBase>());
//            }

//            foreach(var data in outputReport.methodsNeedHook)
//            {
//                if (data.isGeneric)
//                    continue;

//                Type t = Type.GetType(data.type, true);

//                BindingFlags flags = BindingFlags.Default;
//                flags |= data.isPublic ? BindingFlags.Public : BindingFlags.NonPublic;
//                flags |= data.isStatic ? BindingFlags.Static : BindingFlags.Instance;

//                Type[] paramTypes = new Type[data.paramTypes.Length];
//                for(int i = 0, imax = paramTypes.Length; i < imax; i++)
//                {
//                    paramTypes[i] = Type.GetType(data.paramTypes[i], true);
//                }
//                MethodBase method;
//                if (data.isConstructor)
//                    method = t.GetConstructor(flags, null, paramTypes, null);
//                else
//                    method = t.GetMethod(data.name, flags, null, paramTypes, null);

//                if (method == null)
//                    throw new Exception($"can not find method `{data.name}`");

//                if(!methodsToHook.TryGetValue(data.assembly, out List<MethodBase> list))
//                    throw new Exception($"unexpected assembly name `{data.assembly}`");

//                list.Add(method);
//            }
//        }
//    }

//}
