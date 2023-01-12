using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class MonoTestA : MonoBehaviour
{
    public Text text;
    public void ShowText()
    {
        Debug.Log($"MonoTestA.ShowText: text is:\"{text.text}\"");
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
