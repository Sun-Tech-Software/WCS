// Derived from net-object-deep-copy by Alexey Burtsev,
// https://github.com/Burtsev-Alexey/net-object-deep-copy
// Copyright (c) 2014 Burtsev Alexey. Licensed under the MIT License (text in licenses/net-object-deep-copy-LICENSE.txt).
using System;
        
namespace Helper;

public static class ArrayExtensions
{
    #region Public Methods

    public static void ForEach(this Array array, Action<Array, int[]> action)
    {
        if (array.LongLength == 0) return;
        var walker = new ArrayTraverse(array);
        do action(array, walker.Position);
        while (walker.Step());
    }

    #endregion Public Methods
}