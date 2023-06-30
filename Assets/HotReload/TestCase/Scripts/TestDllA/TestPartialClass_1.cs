#define APPLY_PATCH

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 测试有宏时能否解析正确
#if UNITY_2017_4_OR_NEWER
namespace NS_Test
#else
namespace NS_Test_Fake
#endif
{
    namespace NS_Test_Inner
    {
        public struct TestStruct
        {
            public int x;
        }
    }

    namespace NS_Test_Inner2
    {
        public partial class TestPartialClass
        {
            public int x;
            public void DoTest()
            {
#if APPLY_PATCH
                x += 1;
                Debug.Log($"TestPartialClass.DoTest with Patch.x:{x}, y_partial:{y_partial}");
#else
            Debug.Log($"TestPartialClass.DoTest.x:{y_partial}");
#endif
            }
        }
    }
}
