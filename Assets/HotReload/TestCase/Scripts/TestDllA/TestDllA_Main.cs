#define APPLY_PATCH

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace NS_Test
{

    public struct TestStruct
    {
        public static float f;

        public int x;
        public bool y;
    }

    public struct TestStruct2
    {
        public static float f;
        public string propA { get; private set; }

        public event Action<int> evtA;

        public int x;
        public bool y;

        public TestStruct2(int val)
        {
            propA = "private propA value set in constructor";
            evtA = null;
            x = val;
            y = true;
        }
    }

#if APPLY_PATCH
    public class NewTestClass
    {
        public List<List<TestStruct[]>> lst1 = new List<List<TestStruct[]>>();
        public List<List<TestClsG<TestStruct>[]>> lst2 = new List<List<TestClsG<TestStruct>[]>>();
        public List<List<TestClsG<int>[]>[]> lst3 = new List<List<TestClsG<int>[]>[]>();
        public List<List<int[]>> lst4 = new List<List<int[]>>();
        public int[] arrInt = new int[10];
        public static int val;
        public int x;

        static NewTestClass()
        {
            // 请注意，新增类的静态构造函数尽量不要影响其它类数据，且此函数每次被reload都会执行
            val = 2;
            Debug.Log("NewTestClass static constructor");
        }

        public int Add(int y, int z)
        {
            TestStruct.f = 6.28f;
            TestStruct testStruct = new TestStruct();
            testStruct.x = 3;

            TestStruct2 testStruct2 = new TestStruct2(789);
            testStruct2.evtA += x => { };
            string propA = testStruct2.propA;
            Debug.Log(propA.GetType().Name);

            Debug.Log(typeof(TestStruct2).GetProperty("propA").IsSpecialName);

            Debug.Log(typeof(List<List<TestStruct[]>>));
            Debug.Log(typeof(List<List<TestClsG<TestStruct>[]>>));
            Debug.Log(typeof(List<List<TestClsG<int>[]>[]>));
            Debug.Log(typeof(List<List<int[]>>));
            Debug.Log(typeof(int[]));

            int xx = AddG(3, 2.5f);
            var testCls = new TestClsG<float>();
            testCls.FuncA(12);
            testCls.FuncB(34, 5.6f);
            testCls.ShowGA<bool>(7.8f, true);
            testCls.ShowGB(9.1f, 2000);

            return lst1.Count + lst2.Count + lst3.Count + lst4.Count + arrInt.Length + val + x + xx + y + z + 19;
        }

        public int AddG<T>(int xx, T yy)
        {
            return xx + 1;
        }
    }
#endif
    public unsafe class TestCls
    {
        public class InnerTest
        {
            public class Inner2Cls<T> { }

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

        private GameObject _go;
        private InnerTest _innerTest = new InnerTest();
        private TestClsG<TestCls>.TestClsGInner<TestDll_2> _genericFiledTest;

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

        public TestCls(GameObject go)
        {
            _go = go;
        }

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
            TestGeneric();
            TestC();
            Debug.Log($"Test4.val={Test4.val} from Test()");
            str = "be happy";
            Debug.Log(str2);

            _innerTest.FuncInnerA(5);

            int valB = TestDllB_Main.Calc(1, 5);
            Debug.Log($"valB from Ref dll = {valB}");

            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }

        public List<Transform> GetAllDeactiveObjs()
        {
            return new List<Transform>();
        }

        public InnerTest.Inner2Cls<Dictionary<string, Transform>> ReturnNestGenericType()
        {
            return null;
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
            TestGeneric();
            TestC();
            Debug.Log($"Test4.val={Test4.val} from Test()");
            str = "be happy";
            Debug.Log(str2);
            FuncNew();

            _innerTest.FuncInnerA(5);

            int valB = TestDllB_Main.Calc(1, 6);
            Debug.Log($"valB from Ref dll = {valB}");

            var newCls = new NewTestClass();
            newCls.x = 1;
            Debug.Log($"NewTestClass.Add:{newCls.Add(3, 5)}");

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

        public List<Transform> GetAllDeactiveObjs()
        {
            return new List<Transform>(10);
        }

        public InnerTest.Inner2Cls<Dictionary<string, Transform>> ReturnNestGenericType()
        {
            return new InnerTest.Inner2Cls<Dictionary<string, Transform>>();
        }
#endif

        public void TestGeneric()
        {
            // 测试各种当前Assembly和其它Assembly内定义的非泛型和泛型类型
            {
                Debug.Log(typeof(int).Name);
                Debug.Log(typeof(Action<int>).Name);
                Debug.Log(typeof(Action<TestClsG<bool>>).Name);
                Debug.Log(typeof(Action<TestClsG<bool>[]>[]).Name);

                Debug.Log(typeof(TestCls).Name);
                Debug.Log(typeof(TestClsG<>).Name);
                Debug.Log(typeof(TestClsG<>.TestClsGInner<>).Name);
                Debug.Log(typeof(TestClsG<>.TestClsGInner<>).Name);
                Debug.Log(typeof(TestClsG<TestClsG<TestStruct>[]>.TestClsGInner<string[]>[]).Name);
                Debug.Log(typeof(Action<TestClsG<TestClsG<TestCls>[]>.TestClsGInner<string[]>[]>[]).Name);

                int x = 2;
                ref int y = ref x;
                Action<TestClsG<TestCls>.TestClsGInner<string[]>[]>[] x2 = null;
                ref Action<TestClsG<TestCls>.TestClsGInner<string[]>[]>[] y2 = ref x2;

                int * px3 = null;
                ref int * py3 = ref px3;

                TestStruct ts = new TestStruct();
                ref TestStruct ts2 = ref ts;

                TestStruct* pts = null;
                ref TestStruct* pts2 = ref pts; ;
            }

            _genericFiledTest = new TestClsG<TestCls>.TestClsGInner<TestDll_2>();
            _genericFiledTest.innerField_i = 257;
            _genericFiledTest.innerField_V = new TestDll_2();

            var val0 = _genericFiledTest.ShowInner(2);
            var val1 = _genericFiledTest.ShowGInner<double>(this, null, 321.0);
            var val2 = _genericFiledTest.FuncG(this, "test words", null);
            

            var tmpGenericObj = new TestClsG<TestCls>.TestClsGInner<TestDll_2>();
            Func<TestCls, string, TestDll_2, TestDll_2> funcG = tmpGenericObj.FuncG;
            var val3 = funcG(this, "test words 2", null);


            var comp = _go.GetComponent<MonoTestA>();
            comp.ShowText();
            PrintMethodLocation(MethodBase.GetCurrentMethod());
        }

        public void TestC()
        {
            PrintMethodLocation(MethodBase.GetCurrentMethod());
            TestGeneric();
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
            public int innerField_i;
            public V innerField_V;

            public int ShowInner(int x)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.AddComponent<MonoTestA>();
                var val1 = ShowGInner<long>(default(T), default(V), 2345);
                var val2 = FuncG(default(T), "abc", default(V));
#if !APPLY_PATCH
                return x + 1;
#else
                return x + 2;
#endif
            }

            public V ShowGInner<UK>(T arg0, V arg1, UK arg2)
            {
                Debug.Log(typeof(Func<int, bool>));
                Debug.Log(typeof(Func<int, V>));
                Debug.Log(typeof(Func<UK, V>));
                Debug.Log($"ShowInner, T is:{typeof(T).GetType()}, U is:{typeof(UK).GetType()}");
                return arg1;
            }

            public V FuncG(T arg0, string arg1, V arg2)
            {
                return arg2;
            }
        }

        public bool FuncA(int x)
        {
            return x + 1 > 0;
        }

        public bool FuncB(int y, T arg1)
        {
            Debug.Log("FuncB 1");
            Debug.Log(arg1.GetType().FullName);
            return y > 10;
        }

        public bool FuncB(int y, T arg1, List<int> arg2, List<T> arg3, List<List<TestCls>> arg4, List<Dictionary<int, T>> arg5)
        {
            Debug.Log("FuncB 1");
            Debug.Log(arg1.GetType().FullName);
            return y > 10;
        }

        public bool FuncB<U>(int y, T arg1, U arg2)
        {
            Debug.Log("FuncB 1");
            Debug.Log(arg1.GetType().FullName);
            return y > 10;
        }

        public bool FuncB(int y, string str) // 泛型方法允许参数列表和返回值类型与非泛型的一致，调用时默认匹配非泛型方法
        {
            Debug.Log("FuncB 2");
            return true;
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
