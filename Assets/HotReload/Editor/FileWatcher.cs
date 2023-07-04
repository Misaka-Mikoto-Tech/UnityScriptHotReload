using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ScriptHotReload
{
    /// <summary>
    /// 监视cs文件变化
    /// </summary>
    [InitializeOnLoad]
    public class FileWatcher
    {
        public class FileEntry
        {
            public string   path;
            public int      version;
            public DateTime lastModify;
        }

        /// <summary>
        /// 需要监视的目录列表，可自行修改
        /// </summary>
        public static List<string> dirsToWatch = new List<string>() { "Assets" };

        public static bool changedSinceLastGet { get; private set; } = false;
        public static DateTime lastModifyTime { get; private set; } = DateTime.MinValue;

        static Dictionary<string, FileEntry> _filesChanged = new Dictionary<string, FileEntry>();
        static Dictionary<string, FileSystemWatcher> _fileSystemWatchers = new Dictionary<string, FileSystemWatcher>();
        static bool _isWatching = false;
        static object _locker = new object();

        static FileWatcher()
        {
            if(HotReloadConfig.hotReloadEnabled)
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// 获取发生发生改变的文件列表, (FilePath:AssemblyName)
        /// </summary>
        /// <returns></returns>
        public static string[] GetChangedFile()
        {
            var ret = new List<string>((int)(_filesChanged.Count * 1.5f));
            lock(_locker)
            {
                ret.AddRange(_filesChanged.Keys);
                changedSinceLastGet = false;
            }

            for(int i = 0, imax = ret.Count; i < imax; i++)
            {
                string filePath = ret[i];
                string assName = UnityEditor.Compilation.CompilationPipeline.GetAssemblyNameFromScriptPath(filePath);
                Debug.Assert(!string.IsNullOrEmpty(assName));
                ret[i] = $"{filePath}:{Path.GetFileNameWithoutExtension(assName)}";
            }

            return ret.ToArray();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange mode)
        {
            switch(mode)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    StartWatch();
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    StopWatch();
                    break;
                default: break;
            }
        }

        private static void StartWatch()
        {
            _fileSystemWatchers.Clear();
            _filesChanged.Clear();

            HashSet<string> allDirs = new HashSet<string>(dirsToWatch);

            //foreach (string dir in dirsToWatch)
            //{
            //    if (!Directory.Exists(dir))
            //        continue;

            //    // 将子目录的符号链接也加入监控列表
            //    // (.net6 才开始支持 DirectoryInfo.LinkTarget属性，否则只能win/mac单独实现, 因此暂不考虑)
            //    string[] subDirs = Directory.GetDirectories(dir, "*", SearchOption.AllDirectories);
            //    foreach(var subDir in subDirs)
            //    {
            //        var di = new DirectoryInfo(subDir);
            //        if(di.Attributes.HasFlag(FileAttributes.ReparsePoint))
            //        {
                        
            //        }
            //    }
            //}

            foreach (string dir in allDirs)
            {
                var watcher = new FileSystemWatcher(dir, "*.cs");
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.LastWrite;
                watcher.EnableRaisingEvents = true;
                watcher.Changed += OnFileChanged;
                _fileSystemWatchers.Add(dir, watcher);
            }
            _isWatching= true;
        }

        private static void StopWatch()
        {
            _isWatching= false;
            foreach (var watcher in _fileSystemWatchers)
            {
                watcher.Value.Dispose();
            }
            _fileSystemWatchers.Clear();
            _filesChanged.Clear();
        }

        private static void OnFileChanged(object source, FileSystemEventArgs e)
        {
            if (!_isWatching) return; // 此回调函数不在主线程执行，因此不能使用 Application.isPlaying 判断

            var filePath = e.FullPath.Replace('\\', '/').Substring(Environment.CurrentDirectory.Length + 1);
            if (filePath.Contains("/HotReload/Editor/")) return; // 插件自身路径，不reload

            if (HotReloadConfig.fileShouldIgnore(filePath))
                return;

            if (!File.Exists(filePath)) return;

            var now = DateTime.Now;
            lock(_locker)
            {
                if (!_filesChanged.TryGetValue(filePath, out FileEntry entry))
                {
                    entry = new FileEntry() { path = filePath, version = 0, lastModify = now };
                    _filesChanged.Add(filePath, entry);
                }
                entry.version++;
                entry.lastModify = now;
                changedSinceLastGet = true;
                lastModifyTime = now;
            }
        }
    }
}
