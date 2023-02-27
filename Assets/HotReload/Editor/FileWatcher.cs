using System;
using System.Collections;
using System.Collections.Generic;
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
        class FileEntry
        {
            public string name;
            public int version;
            public DateTime lastModify;
        }

        /// <summary>
        /// 需要监视的目录列表，可自行修改
        /// </summary>
        /// <remarks>注意：对符号链接无效，可以考虑递归创建符号链接的watcher</remarks>
        static List<string> dirsToWatch= new List<string>() { "Assets" };

        static FileWatcher()
        {

        }

    }
}
