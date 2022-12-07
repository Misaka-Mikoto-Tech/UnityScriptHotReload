//#define APPLY_PATCH

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

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
            Console.WriteLine(x * 3);
        };

        public Action<int> act2 = x =>
        {
            x += 2;
            Console.WriteLine(x * 3);
            Console.WriteLine(str2);
        };

#if !APPLY_PATCH
        public void Test(out int val)
        {
            val = 2;
            Func<int, bool> f = (int x) => { Console.WriteLine($"{x + 1}-{str}..."); return x > 101; };
            Console.WriteLine($"x is OK:{f(val + 2)}");
            Test2();
            Test3__();
            Console.WriteLine($"Test4.val={Test4.val} from Test()");
            str = "be happy";
            Console.WriteLine(str2);

            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }
#else
        public void Test(out int val)
        {
            val = 2000;
            Func<int, bool> f = (int x) => { Console.WriteLine($"{x + 1}-{str}..."); return x > 101; };
            Console.WriteLine($"x is OK:{f(val + 2)}");
            Test2();
            Test3__();
            Console.WriteLine($"Test4.val={Test4.val} from Test()");
            str = "be happy";
            Console.WriteLine(str2);
            TestNew();

            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }
        public void TestNew()
        {
            //Func<int, bool> f2 = (int x) => { Console.WriteLine($"{x + 1} $$$ {str}..."); return x > 100; };
            //Func<int, bool> f3 = (int x) => { Console.WriteLine($"{x + 1}@@@"); return x > 200; };
            //Console.WriteLine("this is Test3__" + f2(456));

            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }
#endif

        public void Test2()
        {
            // 由于mono的栈帧判断机制，此函数直接被 Test 调用时使用 patch dll 内的定义，套一层时使用原始dll内的定义
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
            Console.WriteLine($"location `<color=yellow>{method.Name}</color>` of current dll: <color=yellow>{assPath.Replace('\\', '/')}</color>");
        }
    }

    public class Test4
    {
        public static int val = 2;
    }
}
