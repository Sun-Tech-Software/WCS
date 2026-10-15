// The deep copy (Copy, InternalCopy, CopyFields, RecursiveCopyBaseTypePrivateFields) is derived from net-object-deep-copy by Alexey Burtsev,
// https://github.com/Burtsev-Alexey/net-object-deep-copy
// Copyright (c) 2014 Burtsev Alexey. Licensed under the MIT License (text in licenses/net-object-deep-copy-LICENSE.txt).
using System.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Serialization;
using System.ComponentModel;

namespace Helper;

public static class ObjectExtensions
{

    #region Private Fields

    private static readonly MethodInfo? CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);

    #endregion Private Fields

    #region Public Methods

    private static T ChangeType<T>(object? value)
    {
        var t = typeof(T);

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            t = Nullable.GetUnderlyingType(t);
        }

        if (t != null && value is IConvertible) return (T)Convert.ChangeType(value, t);
        return default!;
    }

    public static bool ConvertValueToType<T>(object? value, out T? convertFrom)
    {
        if (value is null || value == DBNull.Value)
        {
            convertFrom = default;
            return true;
        }

        if (value is T tValue)
        {
            convertFrom = tValue;
            return true;
        }

        try
        {
            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter.CanConvertFrom(value.GetType()) && converter.IsValid(value))
            {
                convertFrom = (T)converter.ConvertFrom(value)!;
                return true;
            }

            if (value is IConvertible)
            {
                convertFrom = (T)Convert.ChangeType(value, typeof(T));
                return true;
            }
        }
        catch (Exception)
        {
            convertFrom = default;
            return false;
        }

        convertFrom = default;
        return false;
    }

    public static object? Copy(this object originalObject)
    {
        return InternalCopy(originalObject, new Dictionary<object, object?>(new ReferenceEqualityComparer()));
    }

    public static T Copy<T>(this T original)
    {
        if (original == null) return default!;
        return (T)Copy((object)original)!;
    }

    public static object? GetPropertyValue(this object value, string propertyName)
    {
        var valProperties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var sourceProperty = Array.Find(valProperties, prop => prop.Name == propertyName && prop.CanRead);
        return sourceProperty == null ? null : sourceProperty.GetValue(value);
    }

    public static T GetPropertyValue<T>(this object value, string propertyName)
    {
        try
        {
            var valProperties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var sourceProperty = Array.Find(valProperties, prop => prop.Name == propertyName && prop.CanRead);
            var result = sourceProperty?.GetValue(value);
            return (result == null ? default : ChangeType<T>(result))!;
        }
        catch (Exception)
        {
            return default!;
        }
    }

    public static bool IsNumeric(this object value)
    {
        return value is sbyte
               || value is byte
               || value is short
               || value is ushort
               || value is int
               || value is uint
               || value is long
               || value is ulong
               || value is float
               || value is double
               || value is decimal
               || (value is string v && v.All(char.IsNumber));
    }

    public static bool IsPrimitive(this Type type)
    {
        if (type == typeof(string)) return true;
        return type.IsValueType & type.IsPrimitive;
    }

    public static bool IsXmlSerializable(this object? value)
    {
        if (value == null)
            return false;

        try
        {
            var type = value.GetType();
            var serializer = new XmlSerializer(type);
            using var stream = new MemoryStream();
            serializer.Serialize(stream, value);
            return true;
        }
        catch
        {
            return false;
        }
    }
    public static void SetPropertyValue(this object value, string propertyName, object? input)
    {
        if (input == null) return;
        var valProperties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var destinationProperty = Array.Find(valProperties, prop => prop.Name == propertyName && prop.CanWrite && (prop.PropertyType == typeof(object) || prop.PropertyType == input.GetType()));
        if (destinationProperty == null) return;
        destinationProperty.SetValue(value, input);
    }

    #endregion Public Methods

    #region Private Methods

    private static void CopyFields(object originalObject, IDictionary<object, object?> visited, object? cloneObject,
        Type typeToReflect,
        BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                                    BindingFlags.FlattenHierarchy, Func<FieldInfo, bool>? filter = null)
    {
        foreach (var fieldInfo in typeToReflect.GetFields(bindingFlags))
        {
            if (filter != null && filter(fieldInfo) == false) continue;
            if (IsPrimitive(fieldInfo.FieldType)) continue;
            var originalFieldValue = fieldInfo.GetValue(originalObject);
            var clonedFieldValue = InternalCopy(originalFieldValue, visited);
            fieldInfo.SetValue(cloneObject, clonedFieldValue);
        }
    }

    private static object? InternalCopy(object? originalObject, IDictionary<object, object?> visited)
    {
        if (originalObject == null) return null;
        var typeToReflect = originalObject.GetType();
        if (IsPrimitive(typeToReflect)) return originalObject;
        if (visited.TryGetValue(originalObject, out var value)) return value;
        if (typeof(Delegate).IsAssignableFrom(typeToReflect)) return null;
        var cloneObject = CloneMethod?.Invoke(originalObject, null);
        if (typeToReflect.IsArray)
        {
            var arrayType = typeToReflect.GetElementType();
            if (arrayType != null && IsPrimitive(arrayType) == false && cloneObject != null)
            {
                var clonedArray = (Array)cloneObject;
                clonedArray.ForEach((array, indices) =>
                    array.SetValue(InternalCopy(clonedArray.GetValue(indices), visited), indices));
            }
        }

        visited.Add(originalObject, cloneObject);
        CopyFields(originalObject, visited, cloneObject, typeToReflect);
        RecursiveCopyBaseTypePrivateFields(originalObject, visited, cloneObject, typeToReflect);
        return cloneObject;
    }

    private static void RecursiveCopyBaseTypePrivateFields(object originalObject,
        IDictionary<object, object?> visited, object? cloneObject, Type typeToReflect)
    {
        if (typeToReflect.BaseType == null) return;
        RecursiveCopyBaseTypePrivateFields(originalObject, visited, cloneObject, typeToReflect.BaseType);
        CopyFields(originalObject, visited, cloneObject, typeToReflect.BaseType,
            BindingFlags.Instance | BindingFlags.NonPublic, info => info.IsPrivate);
    }

    #endregion Private Methods

}