//#define APPLY_PATCH

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace NS_Test
{
    public unsafe class Test3
    {
        public static int s_val = 123;
        public string str { get; set; } = "default str";
        public static string str2 { get { return (s_val + 2).ToString(); } }

        public Action<int> act = x =>
        {
            x += 2;
            Debug.Log(x * 3);
        };

        public Action<int> act2 = x =>
        {
            x += 2;
            Debug.Log(x * 3);
            Debug.Log(str2);
        };

#if !APPLY_PATCH
        public void Test(out int val)
        {
            val = 2;
            Func<int, bool> f = (int x) => { Debug.Log($"{x + 1}-{str}..."); return x > 101; };
            Debug.Log($"x is OK:{f(val + 2)}");
            Test2();
            Test3__();
            Debug.Log($"Test4.val={Test4.val} from Test()");
            str = "be happy";
            Debug.Log(str2);

            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }
#else
        public void Test(out int val)
        {
            val = 2000;
            Func<int, bool> f = (int x) => { Debug.Log($"{x + 1}-{str}..."); return x > 101; };
            Debug.Log($"x is OK:{f(val + 2)}");
            Test2();
            Test3__();
            Debug.Log($"Test4.val={Test4.val} from Test()");
            str = "be happy";
            Debug.Log(str2);
            TestNew();

            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }
        public void TestNew()
        {
            //Func<int, bool> f2 = (int x) => { Debug.Log($"{x + 1} $$$ {str}..."); return x > 100; };
            //Func<int, bool> f3 = (int x) => { Debug.Log($"{x + 1}@@@"); return x > 200; };
            //Debug.Log("this is Test3__" + f2(456));

            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }
#endif

        public void Test2()
        {
            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }

        public void Test3__()
        {
            PrintMethodLocation(MethodBase.GetCurrentMethod());
            Test2();
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        void PrintMethodLocation(MethodBase method)
        {
            string assPath = method.DeclaringType.Assembly.Location;
            Debug.Log($"location `<color=yellow>{method.Name}</color>` of current dll: <color=yellow>{assPath.Replace('\\', '/')}</color>");
        }
    }

    public class Test4
    {
        public static int val = 2;
    }
}
