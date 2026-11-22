using System;
using System.Collections.Generic;
using UnityEngine;

public class ThreadManager : MonoBehaviour
{
    private static readonly List<Action> executeOnMainThread = new List<Action>();
    private static readonly List<Action> executeCopiedOnMainThread = new List<Action>();

    private void Update()
    {
        UpdateMain();
    }

    public static void ExecuteOnMainThread(Action _action)
    {
        if (_action == null) return;

        lock (executeOnMainThread)
        {
            executeOnMainThread.Add(_action);
        }
    }

    public static void UpdateMain()
    {
        lock (executeOnMainThread)
        {
            if (executeOnMainThread.Count == 0) return;
            executeCopiedOnMainThread.AddRange(executeOnMainThread);
            executeOnMainThread.Clear();
        }

        for (int i = 0; i < executeCopiedOnMainThread.Count; i++)
        {
            try
            {
                executeCopiedOnMainThread[i]();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
        executeCopiedOnMainThread.Clear();
    }
}
