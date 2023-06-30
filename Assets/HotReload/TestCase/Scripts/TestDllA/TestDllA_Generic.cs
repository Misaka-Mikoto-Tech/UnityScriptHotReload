//#define APPLY_PATCH

using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NS_Test_Generic
{
    public class TestCls
    {
        public static int s_val = 123;

        public void FuncA(out int val)
        {
            using var f = File.OpenRead("ProjectSettings/ProjectVersion.txt");
            byte[] buff = new byte[f.Length];
            f.Read(buff, 0, buff.Length);

            string strVer = Encoding.UTF8.GetString(buff);

            val = 22;
            {
                var _genericFiledTest = new TestClsG<object>();
                // 通过泛型类型的非泛型函数间接调用泛型方法 `ShowG`，最终的MethodSpec不会被记录，需要自己处理
                var val0 = _genericFiledTest.Show_Test(0x2345);
            }
            {// 两种不同类型实例间接调用ShowG
                var _genericFiledTest = new TestClsG<int>();
                //var ojb2 = _genericFiledTest.ShowG<object>();
                //var val0 = _genericFiledTest.Show_Test(0x3456);
            }

            EditorUtility.DisplayDialog("title5", "text5", "OK");
        }   
    }

    public class TestClsG<T>
    {
        public int Show_Test(int x)
        {
            EditorUtility.DisplayDialog("title4.3", "text4.3", "OK");
            var val1 = ShowG<object>();
            EditorUtility.DisplayDialog("title4.4", "text4.4", "OK");
            return x + 1;
        }

        public int ShowG<K>()
        {
#if APPLY_PATCH
            // 测试泛型类的泛型方法实例在原始dll内只被间接调用而没有被直接调用的情况下是否能够被patch
            EditorUtility.DisplayDialog("title4.3.5", "ShowG in Patch", "OK");
#endif
            return default;
        }
    }
}
