using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;

namespace StokHouse;

internal static class GameReflection
{
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly ConcurrentDictionary<(Type Type, string Name, bool Static), MemberInfo> ReadableMembers = new();

    internal static Type Type(string name) =>
        AccessTools.TypeByName(name) is { } type && type.FullName == name
            ? type
            : throw new TypeLoadException($"Stok House could not bind game type {name}.");

    internal static MethodInfo Method(Type type, string name, BindingFlags scope, params Type[] parameters)
    {
        // Parameter types alone can't distinguish getCharacterFromName(...) from getCharacterFromName<T>(...).
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            var matches = current.GetMethods(DeclaredMembers | scope)
                .Where(method => method.Name == name && !method.IsGenericMethod &&
                                 method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters))
                .ToArray();
            if (matches.Length == 1)
                return matches[0];
            if (matches.Length > 1)
                throw new AmbiguousMatchException($"Multiple non-generic matches for {Describe(type, name, scope, parameters)}.");
            if ((scope & BindingFlags.DeclaredOnly) != 0)
                break;
        }
        throw new MissingMethodException($"Could not bind {Describe(type, name, scope, parameters)}.");
    }

    internal static object? Read(object target, string name)
    {
        Type? staticType = target as Type;
        var member = ReadableMember(staticType ?? target.GetType(), name, staticType is not null);
        object? instance = staticType is null ? target : null;
        return member is PropertyInfo property ? property.GetValue(instance) : ((FieldInfo)member).GetValue(instance);
    }

    internal static Type MemberType(Type type, string name) => ReadableMember(type, name, false) switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => throw new InvalidOperationException()
    };

    private static MemberInfo ReadableMember(Type type, string name, bool isStatic) =>
        ReadableMembers.GetOrAdd((type, name, isStatic), key =>
        {
            var flags = DeclaredMembers | (key.Static ? BindingFlags.Static : BindingFlags.Instance);
            for (Type? current = key.Type; current is not null; current = current.BaseType)
            {
                var properties = current.GetProperties(flags).Where(property => property.Name == key.Name &&
                    property.GetIndexParameters().Length == 0 && property.GetGetMethod(nonPublic: true) is not null).ToArray();
                if (properties.Length > 1)
                    throw new AmbiguousMatchException($"Multiple readable properties match {key.Type.FullName}.{key.Name}.");
                if (properties.Length == 1)
                    return properties[0];
                if (current.GetField(key.Name, flags) is { } field)
                    return field;
            }
            throw new MissingMemberException($"Could not bind readable {(key.Static ? "static" : "instance")} member {key.Type.FullName}.{key.Name}.");
        });

    internal static string Signature(MethodInfo method) => Describe(method.DeclaringType!, method.Name,
        method.IsStatic ? BindingFlags.Static : BindingFlags.Instance,
        method.GetParameters().Select(parameter => parameter.ParameterType));

    private static string Describe(Type type, string name, BindingFlags scope, IEnumerable<Type> parameters) =>
        $"{((scope & BindingFlags.Static) != 0 ? "static" : "instance")} {type.FullName}.{name}({string.Join(", ", parameters.Select(parameter => parameter.FullName))})";
}
