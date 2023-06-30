using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface ITestInherit
{
    void Tick();
}

public class TestInherit1 : ITestInherit
{
    public void Tick()
    {
        UnityEngine.Debug.Log($"Fffff667FffffF");
    }
}
public class TestInherit : MonoBehaviour
{
    private TestInherit1 t1 = new TestInherit1();
    // Start is called before the first frame update
    void Start()
    {
        Debug.Log($"ff3355");
    }

    // Update is called once per frame
    void Update()
    {
        t1.Tick();
    }
}
