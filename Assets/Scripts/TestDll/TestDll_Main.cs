//#define APPLY_PATCH

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace NS_Test
{
#if APPLY_PATCH
    public class NewTestClass
    {
        public static int val;
        public int x;
        static NewTestClass()
        {
            // 请注意，新增类的静态构造函数尽量不要影响其它类数据
            val = 2;
            Debug.Log("NewTestClass static constructor");
        }

        public int Add(int y)
        {
            return x + y + val;
        }
    }
#endif
    public unsafe class TestCls
    {
        private class InnerTest
        {
            public int innerX;
            public void FuncInnerA(int val)
            {
#if !APPLY_PATCH
                Debug.Log($"<color=yellow>FuncInnerA:</color> {innerX + val}");
#else
                Debug.Log($"<color=yellow>FuncInnerA: patched</color> {innerX + val + 1}");
#endif
            }
        }

        public static int s_val = 123;
        public string str { get; set; } = "default str";
        public static string str2 { get { return (s_val + 2).ToString(); } }

        private InnerTest _innerTest = new InnerTest();

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

        public void Init()
        {
            _innerTest.innerX = 10;
        }

#if !APPLY_PATCH
        static TestCls()
        {
            Debug.Log("static constructor");
        }

        public void FuncA(out int val)
        {
            val = 2;
            Func<int, bool> f = (int x) => { Debug.Log($"{x + 1}-{str}..."); return x > 101; };
            Debug.Log($"x is OK:{f(val + 2)}");
            TestB();
            TestC();
            Debug.Log($"Test4.val={Test4.val} from Test()");
            str = "be happy";
            Debug.Log(str2);

            _innerTest.FuncInnerA(5);

            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }
#else
        static TestCls()
        {
            Debug.Assert(false, "static constructor of patched type can not be invoke");
            Debug.Log("static constructor patched");
        }

        public void FuncA(out int val)
        {
            val = 2000;
            Func<int, bool> f = (int x) => { Debug.Log($"{x + 1}-{str}..."); return x > 101; };
            Debug.Log($"x is OK:{f(val + 2)}");
            TestB();
            TestC();
            Debug.Log($"Test4.val={Test4.val} from Test()");
            str = "be happy";
            Debug.Log(str2);
            FuncNew();

            _innerTest.FuncInnerA(5);

            var newCls = new NewTestClass();
            newCls.x = 1;
            Debug.Log($"NewTestClass.Add:{newCls.Add(3)}");

            var test2 = new TestDll_2();
            int z = test2.Mul_2(5, 6);
            Debug.Log($"test2={z}");

            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }
        public void FuncNew()
        {
            //Func<int, bool> f2 = (int x) => { Debug.Log($"{x + 1} $$$ {str}..."); return x > 100; };
            //Func<int, bool> f3 = (int x) => { Debug.Log($"{x + 1}@@@"); return x > 200; };
            //Debug.Log("this is Test3__" + f2(456));

            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }

        // add new virtual method to exists type is not allowd
        //public virtual void FuncVirtualNew()
        //{
        //}
#endif

        public void TestB()
        {
            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }

        public void TestC()
        {
            PrintMethodLocation(MethodBase.GetCurrentMethod());
            TestB();
        }

        public T TestG<T>(T t) where T:new()
        {
            Debug.Log($"t.type is:{t.GetType()}");
            return new T();
        }

        void PrintMethodLocation(MethodBase method)
        {
            var currMethod = MethodBase.GetCurrentMethod();
            string assPath = method.DeclaringType.Assembly.Location.Substring(Environment.CurrentDirectory.Length + 1);
            Debug.Log($"location `<color=yellow>{method.Name}</color>` of current dll: <color=yellow>{assPath.Replace('\\', '/')}</color>");
        }
    }

    public class Test4
    {
        public static int val = 2;
    }

    public class TestClsG<T>
    {
        public class TestClsGInner<V>
        {
            public int ShowInner(int x)
            {
                return x + 1;
            }

            public V ShowGInner<U>(T arg0, V arg1, U arg2)
            {
                Debug.Log($"ShowInner, T is:{typeof(T).GetType()}, U is:{typeof(U).GetType()}");
                return arg1;
            }
        }

        public string str;
        public T ShowGA<U>(T arg0, U arg1)
        {
            Debug.Log($"ShowA, T is:{typeof(T).GetType()}, U is:{typeof(U).GetType()}");
            return arg0;
        }

        public U ShowGB<U>(T arg0, U arg1)
        {
            Debug.Log($"ShowB, T is:{typeof(T).GetType()}, U is:{typeof(U).GetType()}");
            return arg1;
        }
    }
}
