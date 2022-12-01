using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class Test : MonoBehaviour
{
    public Button btnTest;

    // Start is called before the first frame update
    void Start()
    {
        btnTest.onClick.AddListener(OnBtnTest);
        NS_Test.Test3.s_val = 456;
        NS_Test.Test4.val = 1234;
    }

    void OnBtnTest()
    {
        Debug.Log($"Test3.s_val={NS_Test.Test3.s_val}");

        var test = new NS_Test.Test3();
        test.Test(out int val);
        Debug.Log($"OnBtnTest:val={val}");

        Debug.Log($"Test3.s_val={NS_Test.Test3.s_val}");
    }

    // Update is called once per frame
    void Update()
    {
        transform.position += new Vector3(0, 0, 0.1f);
    }
}

namespace NS_Test
{
    public class Test3
    {
        public static int s_val = 123;
        public string str { get; private set; }
        public static string str2 { get { return (s_val + 2).ToString(); }}

        public Action<int> act = x =>
        {
            x += 2;
            Debug.Log(x * 3);
        };

        public void Test(out int val)
        {
            val = 2;
            Func<int, bool> f = (int x) => { Debug.Log($"{x + 1}..."); return x > 101; };
            Debug.Log($"x is OK:{f(val + 2)}");
            Test2();
            Debug.Log($"Test4.val={Test4.val} from Test()");
            str = "be happy";
            Debug.Log(str2);
            //Test3__();
        }

        public void Test2()
        {
            string assPath = new Action(Test2).Method.DeclaringType.Assembly.Location;
            Debug.Log($"location of current dll:{assPath}");
        }

        //public void Test3__()
        //{
        //    Debug.Log("this is Test3__");
        //}

        public string TestG<T>(out int val1, T val2) where T : UnityEngine.Object
        {
            val1 = 123;
            act(val1);
            return val2.name;
        }
    }

    public class Test4
    {
        public static int val = 2;
    }
}
