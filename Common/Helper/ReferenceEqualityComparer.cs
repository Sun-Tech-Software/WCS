// Derived from net-object-deep-copy by Alexey Burtsev,
// https://github.com/Burtsev-Alexey/net-object-deep-copy
// Copyright (c) 2014 Burtsev Alexey. Licensed under the MIT License (text in licenses/net-object-deep-copy-LICENSE.txt).
using System.Collections.Generic;

namespace Helper;

public class ReferenceEqualityComparer : EqualityComparer<object>
{
    #region Public Methods

    public override bool Equals(object? x, object? y)
    {
        return ReferenceEquals(x, y);
    }
    public override int GetHashCode(object? obj)
    {
        return obj == null ? 0 : obj.GetHashCode();
    }

    #endregion Public Methods
}