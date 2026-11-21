using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace StokHouse.FamilyBudget.Spec;

internal sealed class AssemblyContract : ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader metadata;

    internal AssemblyContract(MetadataReader metadata) => this.metadata = metadata;

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "function pointer";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(", ", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        MoneyAudit.TypeName(reader, handle);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        MoneyAudit.TypeName(reader, handle);
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    internal bool ReferenceOnly => metadata.GetAssemblyDefinition().GetCustomAttributes()
        .Any(handle => MoneyAudit.Resolve(metadata, metadata.GetCustomAttribute(handle).Constructor) ==
            "System.Runtime.CompilerServices.ReferenceAssemblyAttribute..ctor");

    internal IEnumerable<string> Describe(IEnumerable<string> members)
    {
        var names = members.ToHashSet(StringComparer.Ordinal);
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            string type = MoneyAudit.TypeName(metadata, typeHandle);
            var definition = metadata.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in definition.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                string full = type + "." + metadata.GetString(method.Name);
                if (!names.Contains(full))
                    continue;
                var signature = method.DecodeSignature(this, null);
                string scope = signature.Header.IsInstance ? "instance" : "static";
                yield return $"{scope} {signature.ReturnType} {full}({string.Join(", ", signature.ParameterTypes)})";
            }
            foreach (var fieldHandle in definition.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                string full = type + "." + metadata.GetString(field.Name);
                if (names.Contains(full))
                    yield return $"{field.DecodeSignature(this, null)} {full}";
            }
        }
    }
}
