//#define APPLY_PATCH

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NS_Test
{
    public class TestInternal_Main
    {
        TestInternal_Define _def;
        public TestInternal_Main()
        {
            _def = new TestInternal_Define();
            _def.TestDiffType(this); // 目前这个调用会出现，正尝试解决
#if APPLY_PATCH
            Debug.Log(def.x);
#else
#endif
        }
    }
}
