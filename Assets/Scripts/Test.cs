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
        test.FuncA(out int val);
        Debug.Log($"OnBtnTest:val={val}");

        Debug.Log($"Test3.s_val={NS_Test.Test3.s_val}");
    }

    // Update is called once per frame
    void Update()
    {
        transform.position += new Vector3(0, 0, 0.1f);
    }
}