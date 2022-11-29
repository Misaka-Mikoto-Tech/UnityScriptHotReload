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

        public Action<int> act = x =>
        {
            x += 2;
            Debug.Log(x * 3);
        };

        public void Test(out int val)
        {
            val = 2000;
            Func<int, bool> f = (int x) => { Debug.Log($"{x + 1}..."); return x > 101; };
            Debug.Log($"<color=yellow>x is OK:{f(val + 2)}</color>");
            Test2();
        }

        public void Test2()
        {
            string assPath = new Action(Test2).Method.DeclaringType.Assembly.Location;
            Debug.Log($"location of current dll:{assPath}");
        }

        public string TestG<T>(out int val1, T val2) where T : UnityEngine.Object
        {
            val1 = 123;
            act(val1);
            return val2.name;
        }
    }
}
