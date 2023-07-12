using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NS_Test
{
    public class TestInternal_Define
    {
        public int x;

        public void TestDiffType(TestInternal_Main main)
        {
            Debug.Log(main.GetType().FullName);
        }
    }
}
