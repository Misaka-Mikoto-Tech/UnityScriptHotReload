//#define PATCHER_DEBUG

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
using System.Text;

namespace ScriptHotReload
{
    [InitializeOnLoad]
    public class HotReloadExecutor
    {
        const string kMenue_HotReload = "Tools/HotReload/是否自动重载";

        public static int patchNo { get; private set; } = 0;
        static string _dotnetPath;
        static string _cscPath;

        static Task<int> _patchTask;
        static ConcurrentQueue<string> _patchTaskOutput = new ConcurrentQueue<string>();
        static Dictionary<string, List<MethodBase>> _methodsToHook = new Dictionary<string, List<MethodBase>>(); // <AssemblyName, List>

        /// <summary>
        /// 是否是自动重载模式
        /// </summary>
        public static bool autoReloadMode
        {
            get
            {
                return EditorPrefs.GetBool(kMenue_HotReload, false);
            }
            private set
            {
                EditorPrefs.SetBool(kMenue_HotReload, value);
            }
        }

        #region 菜单功能
        /// <summary>
        /// 重载事件是否已触发（auto模式下将始终触发）
        /// </summary>
        static bool reloadEventFired;

        [MenuItem("Tools/HotReload/立即重载 (Play时有效) #R")]
        static void Menu_ManualReload()
        {
            reloadEventFired = true;
        }

        /// <summary>
        /// 切换 [自动重载] 菜单函数
        /// </summary>
        [MenuItem(kMenue_HotReload, false)] // "Tools/HotReload/是否自动重载"
        static void Menue_SwapAutoReloadMode()
        {
            bool isChecked = Menu.GetChecked(kMenue_HotReload);
            isChecked = !isChecked;

            autoReloadMode = isChecked;
            Menu.SetChecked(kMenue_HotReload, isChecked);

            // 切换到自动模式时主动设置触发初始值为true, 反之手动模式初始不触发
            reloadEventFired = isChecked;
        }

        [MenuItem(kMenue_HotReload, true)]
        static bool Menue_AutoReloadMode_Check()
        {
            Menu.SetChecked(kMenue_HotReload, autoReloadMode);
            return true;
        }
        #endregion

        static HotReloadExecutor()
        {
            if (!HotReloadConfig.hotReloadEnabled)
                return;

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

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange mode)
        {
            switch (mode)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    // 进入播放模式后，首先删除所有之前残留的patch文件
                    foreach(string file in Directory.GetFiles(HotReloadConfig.kTempScriptDir))
                    {
                        if (file.EndsWith(".dll") || file.EndsWith(".pdb"))
                        {
                            try
                            {
                                File.Delete(file);
                            }
                            catch(Exception ex)
                            {
                                UnityEngine.Debug.LogError($"delete patch file fail:{ex.Message}");
                            }
                        }
                            
                    }
                    break;
                default: break;
            }
        }

        private static void OnEditorUpdate()
        {
            if (!(HotReloadConfig.hotReloadEnabled && Application.isPlaying))
                return;

            if (_patchTask != null)
            {
                DispatchTaskOutput();
                if (_patchTask.IsCompleted)
                {
                    if(_patchTask.Result == 0)
                    {
                        try
                        {
                            HookAssemblies.DoHook(_methodsToHook);
                            if (_methodsToHook.Count > 0)
                                patchNo++;

                            UnityEngine.Debug.Log("<color=yellow>热重载完成</color>");
                        }
                        catch(Exception ex)
                        {
                            HookAssemblies.UnHook(_methodsToHook);
                            UnityEngine.Debug.LogErrorFormat("热重载出错:{0}\r\n{1}", ex.Message, ex.StackTrace);
                        }
                    }
                    else
                    {
                        UnityEngine.Debug.LogErrorFormat("生成Patch出错, 停止重载");
                    }
                    _patchTask = null;
                }
                return;
            }

            if(autoReloadMode)
            {
                if (!FileWatcher.changedSinceLastGet
                || new TimeSpan(DateTime.Now.Ticks - FileWatcher.lastModifyTime.Ticks).TotalSeconds < HotReloadConfig.kFileChangeCheckSpan)
                    return;
            }
            else
            {
                try
                {
                    if (!reloadEventFired)
                        return;
                    else if (!FileWatcher.changedSinceLastGet)
                    {
                        UnityEngine.Debug.LogWarning("没有文件发生改变，不执行热重载");
                        return;
                    }
                }
                finally
                {
                    reloadEventFired = false;
                }
            }

            GenPatcherInputArgsFile();
            _patchTask = Task.Run(RunAssemblyPatchProcess);
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
            public string patchDllPathFormat;
            public string lambdaWrapperBackend;

            public string[] filesChanged;

            public string[] defines;
            public string[] allAssemblyPathes;
        }
        static void GenPatcherInputArgsFile()
        {
            var inputArgs = new InputArgs();
            inputArgs.patchNo = patchNo;
            inputArgs.workDir = Environment.CurrentDirectory.Replace('\\', '/');
            inputArgs.dotnetPath = _dotnetPath;
            inputArgs.cscPath = _cscPath;
            
            inputArgs.tempScriptDir = HotReloadConfig.kTempScriptDir;
            inputArgs.builtinAssembliesDir = HotReloadConfig.kBuiltinAssembliesDir;
            inputArgs.patchDllPathFormat = HotReloadConfig.kPatchDllPathFormat;
            inputArgs.lambdaWrapperBackend = HotReloadConfig.kLambdaWrapperBackend;

            inputArgs.filesChanged = FileWatcher.GetChangedFile();

            inputArgs.defines = EditorUserBuildSettings.activeScriptCompilationDefines;
            inputArgs.allAssemblyPathes = GetAllAssemblyPaths();

            string jsonStr = JsonUtility.ToJson(inputArgs, true);
            File.WriteAllText(HotReloadConfig.kAssemblyPatcherInput, jsonStr, Encoding.UTF8);
        }

        private static int RunAssemblyPatchProcess()
        {
            var startInfo = new ProcessStartInfo();
            startInfo.FileName = Path.GetDirectoryName(GetThisFilePath()) + "/AssemblyPatcher~/AssemblyPatcher.exe";
#if PATCHER_DEBUG
            startInfo.Arguments = $"{HotReloadConfig.kAssemblyPatcherInput} {HotReloadConfig.kAssemblyPatcherOutput} debug";
            startInfo.CreateNoWindow = false;
#else
            startInfo.Arguments = $"{HotReloadConfig.kAssemblyPatcherInput} {HotReloadConfig.kAssemblyPatcherOutput}";
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
            {
                try
                {
                    ParseOutputReport();
                }
                catch(Exception ex)
                {
                    _patchTaskOutput.Enqueue("[Error][ParseOutput] " + ex.Message);
                    exitCode = -2;
                }
            }

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
                public string assembly;
                public bool isConstructor;
                public bool isGeneric;
                public bool isPublic;
                public bool isStatic;
                public bool isLambda;
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

            if (!File.Exists(HotReloadConfig.kAssemblyPatcherOutput))
                throw new Exception($"can not find output report file `{HotReloadConfig.kAssemblyPatcherOutput}`");

            string text = File.ReadAllText(HotReloadConfig.kAssemblyPatcherOutput);
            var outputReport = JsonUtility.FromJson<OutputReport>(text);

            foreach (var data in outputReport.methodsNeedHook)
            {
                if (data.isGeneric) // 泛型方法暂时不处理
                    continue;

                if (data.isStatic && data.isConstructor) // .cctor
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

                List<MethodBase> lst;
                if(!_methodsToHook.TryGetValue(data.assembly, out lst))
                {
                    lst = new List<MethodBase>();
                    _methodsToHook.Add(data.assembly, lst);
                }
                lst.Add(method);
            }
        }
    }

}
