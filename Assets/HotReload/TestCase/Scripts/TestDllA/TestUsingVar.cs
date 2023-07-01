#define APPLY_PATCH

using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NS_Test
{
    public class TestUsingVar
    {

        public static string ReadProjVersion()
        {
            string path = "ProjectSettings/ProjectVersion.txt";
            string ret = string.Empty;


            // 有些低版本 Unity 带的mono默认参数不支持 using var
#if APPLY_PATCH
        using var f = File.OpenRead(path);
        using var sr = new StreamReader(f);
        ret = sr.ReadToEnd();
#else
            using (var f = File.OpenRead(path))
            {
                using (var sr = new StreamReader(f))
                {
                    ret = sr.ReadToEnd();
                }
            }
#endif
            return ret;
        }
    }

}

