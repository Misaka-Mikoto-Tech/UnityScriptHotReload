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


            // ��Щ�Ͱ汾 Unity ����monoĬ�ϲ�����֧�� using var
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

