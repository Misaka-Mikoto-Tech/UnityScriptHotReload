using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Test : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
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
        }

        public string TestG<T>(out int val1, T val2) where T : UnityEngine.Object
        {
            val1 = 123;
            act(val1);
            return val2.name;
        }
    }
}
