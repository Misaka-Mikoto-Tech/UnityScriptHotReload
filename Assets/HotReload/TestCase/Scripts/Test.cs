using MonoHook;
using NS_Test;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UI;

public class Test : MonoBehaviour
{
    public Button btnTest;
    public Button btnApplyPatch;
    public Button btnPartialClass;

    public void GFuncA<T>(T obj)
    {
        Debug.Log(obj.GetType().Name);
    }

    // Start is called before the first frame update
    void Start()
    {
        //var gmi = new Action<string>(GFuncA<string>).Method.GetGenericMethodDefinition();
        //var miA = gmi.MakeGenericMethod(typeof(string));
        //var miB = gmi.MakeGenericMethod(typeof(Debug));
        //var miC = gmi.MakeGenericMethod(typeof(bool));
        //var miD = gmi.MakeGenericMethod(typeof(int));
        //var miE = gmi.MakeGenericMethod(typeof(uint));
        //var miF = gmi.MakeGenericMethod(typeof(float));

        //Debug.Log($"miA:{miA.MethodHandle.GetFunctionPointer().ToInt64():X}");
        //Debug.Log($"miB:{miB.MethodHandle.GetFunctionPointer().ToInt64():X}");
        //Debug.Log($"miC:{miC.MethodHandle.GetFunctionPointer().ToInt64():X}");
        //Debug.Log($"miD:{miD.MethodHandle.GetFunctionPointer().ToInt64():X}");
        //Debug.Log($"miE:{miE.MethodHandle.GetFunctionPointer().ToInt64():X}");
        //Debug.Log($"miF:{miF.MethodHandle.GetFunctionPointer().ToInt64():X}");

        btnTest.onClick.AddListener(OnBtnTest);
        btnApplyPatch.onClick.AddListener(OnBtnApplyPatch);
        btnPartialClass.onClick.AddListener(OnBtnPartialClass);

        NS_Test.TestCls.s_val = 456;
        NS_Test.Test4.val = 1234;
    }

    void OnBtnTest()
    {
        Debug.Log($"Test3.s_val={NS_Test_Generic.TestCls.s_val}");

        var test = new NS_Test_Generic.TestCls();
        test.FuncA(out int val);
        Debug.Log($"OnBtnTest:val={val}");

        Debug.Log($"Test3.s_val={NS_Test_Generic.TestCls.s_val}");

        // test generic hook
        //{
        //    // 默认只 hook 引用类型的类型及其方法，除非手动指定值类型方法实例
        //    // 经过测试，泛型类型实例中，无论其自身还是所属类型，只要有一个参数是值类型，那么最终的实例就是独立的，即使是同一值类型的不同引用类型参数的方法

        //    var clsStr = new TestClsG<string>();
        //    var clsObj = new TestClsG<object>(); // 这两个应该对应同一个地址和虚表
        //    var clsInt = new TestClsG<int>(); // 这个是独立的

        //    clsInt.FuncB(2, 3);

        //    var sG = Type.GetType("NS_Test.TestClsG`1, TestDllA");
        //    var tStr = typeof(TestClsG<string>);
        //    var tObj = typeof(TestClsG<object>);
        //    var tInt = typeof(TestClsG<int>);

        //    var miGG = sG.GetMethod("ShowGA");
        //    //var miGG2 = sG.GetMethod("FuncB");
        //    var miStrG = tStr.GetMethod("ShowGA");
        //    var miObjG = tObj.GetMethod("ShowGA");
        //    var miIntG = tInt.GetMethod("ShowGA");

        //    var miStrStr = miStrG.MakeGenericMethod(typeof(string));
        //    var miStrObj = miStrG.MakeGenericMethod(typeof(object));
        //    var miStrBool = miStrG.MakeGenericMethod(typeof(bool));

        //    var miObjObj = miObjG.MakeGenericMethod(typeof(object));

        //    var miIntStr = miIntG.MakeGenericMethod(typeof(string));
        //    var miIntObj = miIntG.MakeGenericMethod(typeof(object));
        //    var miIntBool = miIntG.MakeGenericMethod(typeof(bool));

        //    Debug.Log($"{nameof(miStrStr)}:{miStrStr.MethodHandle.GetFunctionPointer().ToInt64():X}");
        //    Debug.Log($"{nameof(miStrObj)}:{miStrObj.MethodHandle.GetFunctionPointer().ToInt64():X}");
        //    Debug.Log($"{nameof(miStrBool)}:{miStrBool.MethodHandle.GetFunctionPointer().ToInt64():X}");

        //    Debug.Log($"{nameof(miObjObj)}:{miObjObj.MethodHandle.GetFunctionPointer().ToInt64():X}");

        //    Debug.Log($"{nameof(miIntStr)}:{miIntStr.MethodHandle.GetFunctionPointer().ToInt64():X}");
        //    Debug.Log($"{nameof(miIntObj)}:{miIntObj.MethodHandle.GetFunctionPointer().ToInt64():X}");
        //    Debug.Log($"{nameof(miIntBool)}:{miIntBool.MethodHandle.GetFunctionPointer().ToInt64():X}");

        //    //var ret1 = clsStr.ShowGA<string>("abc", "efg");
        //    //var ret2 = clsStr.ShowGA<object>("aaa", new object()); // 这两个应该是同一个地址

        //    string typeName = "NS_Test.TestClsG`1, TestDllA";
        //    Type type = ParseType(typeName);

        //}
        
    }

    void OnBtnApplyPatch()
    {
        MethodHook.onlyShowAddr = false;
        MethodHook.ProcessWaitHooks();
    }

    void OnBtnPartialClass()
    {
        var partialClass = new NS_Test.TestPartialClass();
        partialClass.x = 3;
        partialClass.y_partial = "partial test str";
        partialClass.DoTest();
    }

    static Type ParseType(string typeName)
    {
        Type ret = Type.GetType(typeName);
        if (ret == null)
            return null;

        if (ret.ContainsGenericParameters)
        {
            // 我们目标只hook引用类型，值类型每个类型都有不同内存地址，遍历所有类型不划算
            Type[] args = ret.GetGenericArguments();
            for (int i = 0, imax = args.Length; i < imax; i++)
                args[i] = typeof(object);

            ret = ret.MakeGenericType(args);
        }

        return ret;
    }

    // Update is called once per frame
    void Update()
    {
        transform.position += new Vector3(0, 0, 0.1f);
    }
}