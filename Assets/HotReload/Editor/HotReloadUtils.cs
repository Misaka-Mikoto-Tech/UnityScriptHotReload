using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System.Security.Cryptography;
using System;
using System.Reflection;
using Mono.Cecil;
using UnityEditor;

namespace ScriptHotReload
{
    public static class HotReloadUtils
    {
        public static bool IsFilesEqual(string lpath, string rpath)
        {
            if (!File.Exists(lpath) || !File.Exists(rpath))
                return false;

            long lLen = new FileInfo(lpath).Length;
            long rLen = new FileInfo(rpath).Length;
            if (lLen != rLen)
                return false;

            return GetMd5OfFile(lpath) == GetMd5OfFile(rpath);
        }

        public static string GetMd5OfFile(string filePath)
        {
            string fileMd5 = null;
            try
            {
                using (var fs = File.OpenRead(filePath))
                {
                    var md5 = MD5.Create();
                    var md5Bytes = md5.ComputeHash(fs);
                    fileMd5 = BitConverter.ToString(md5Bytes).Replace("-", "").ToLower();
                }
            }
            catch { }
            return fileMd5;
        }

        public static void RemoveAllFiles(string dir)
        {
            string[] files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
                File.Delete(file);
        }

        public static BindingFlags BuildBindingFlags(MethodDefinition definition)
        {
            BindingFlags flags = BindingFlags.Default;
            if (definition.IsPublic)
                flags |= BindingFlags.Public;
            else
                flags |= BindingFlags.NonPublic;

            if (definition.IsStatic)
                flags |= BindingFlags.Static;
            else
                flags |= BindingFlags.Instance;

            return flags;
        }

        /// <summary>
        /// 以 MethodDefinition 为参数或者 MethodInfo
        /// </summary>
        /// <param name="t"></param>
        /// <param name="definition"></param>
        /// <returns></returns>
        /// <remarks>TODO 优化性能</remarks>
        public static MethodInfo GetMethodInfoSlow(Type t, MethodDefinition definition)
        {
            MethodInfo[] mis = t.GetMethods(BuildBindingFlags(definition));
            ParameterDefinition[] defParaArr = definition.Parameters.ToArray();
            foreach(var mi in mis)
            {
                if (mi.ReturnType.FullName != definition.ReturnType.FullName)
                    continue;

                ParameterInfo[] piArr = mi.GetParameters();
                if(piArr.Length == defParaArr.Length)
                {
                    bool found = true;
                    for(int i = 0, imax = piArr.Length; i < imax; i++)
                    {
                        var defPara = defParaArr[i];
                        var pi = piArr[i];
                        if (pi.ParameterType.FullName != defPara.ParameterType.FullName)
                        {
                            found = false;
                            break;
                        }
                    }

                    if(found)
                        return mi;
                }
            }
            return null;
        }
    }

}
