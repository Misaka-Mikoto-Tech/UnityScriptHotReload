using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

using static ScriptHotReload.HotReloadConfig;

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

        public static bool hasChangedSinceLast { get; private set; } = false;
        public static DateTime lastModifyTime { get; private set; } = DateTime.MinValue;
        public static Dictionary<string, FileEntry> filesChanged { get; private set; } = new Dictionary<string, FileEntry>();

        /// <summary>
        /// 需要监视的目录列表，可自行修改
        /// </summary>
        public static List<string> dirsToWatch= new List<string>() { "Assets" };

        static Dictionary<string, FileSystemWatcher> _fileSystemWatchers = new Dictionary<string, FileSystemWatcher>();
        

        static FileWatcher()
        {
            if(hotReloadEnabled)
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
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
            }
            
        }

        private static void StopWatch()
        {
            foreach (var watcher in _fileSystemWatchers)
            {
                watcher.Value.Dispose();
            }
            _fileSystemWatchers.Clear();
        }

        private static void OnFileChanged(object source, FileSystemEventArgs e)
        {
            var fullPath = e.FullPath;

            if (!Application.isPlaying) return;
            if(!File.Exists(fullPath)) return;

            if(!filesChanged.TryGetValue(fullPath, out FileEntry entry))
            {
                entry = new FileEntry() { path = fullPath, version = 0, lastModify = DateTime.Now };
                filesChanged.Add(fullPath, entry);
            }
            entry.version++;
            entry.lastModify= DateTime.Now;
            hasChangedSinceLast = true;
        }
    }
}
