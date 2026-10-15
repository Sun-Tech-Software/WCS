// Derived from net-object-deep-copy by Alexey Burtsev,
// https://github.com/Burtsev-Alexey/net-object-deep-copy
// Copyright (c) 2014 Burtsev Alexey. Licensed under the MIT License (text in licenses/net-object-deep-copy-LICENSE.txt).
using System;

namespace Helper;

internal class ArrayTraverse
{
    #region Private Fields

    private readonly int[] _maxLengths;

    #endregion Private Fields

    #region Public Fields

    public readonly int[] Position;

    #endregion Public Fields

    #region Public Constructors

    public ArrayTraverse(Array array)
    {
        _maxLengths = new int[array.Rank];
        for (var i = 0; i < array.Rank; ++i) _maxLengths[i] = array.GetLength(i) - 1;
        Position = new int[array.Rank];
    }

    #endregion Public Constructors

    #region Public Methods

    public bool Step()
    {
        for (var i = 0; i < Position.Length; ++i)
            if (Position[i] < _maxLengths[i])
            {
                Position[i]++;
                for (var j = 0; j < i; j++) Position[j] = 0;
                return true;
            }

        return false;
    }

    #endregion Public Methods
}