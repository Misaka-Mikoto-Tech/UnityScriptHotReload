using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NS_Test
{
    class TestInternal_Define
    {
        internal int x;

        public void TestDiffType(TestInternal_Main main)
        {
            Debug.Log(main.GetType().FullName);
        }
    }
}
