using System.Buffers.Binary;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace StokHouse.FamilyBudget.Spec;

internal static class MoneyAudit
{
    private static readonly IReadOnlyDictionary<ushort, OperandType> OperandTypes =
        typeof(OpCodes).GetFields()
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(op => unchecked((ushort)op.Value), op => op.OperandType);

    private static readonly HashSet<string> Targets = new(StringComparer.Ordinal)
    {
        "StardewValley.Farmer.get_Money", "StardewValley.Farmer.set_Money",
        "StardewValley.Farmer.get__money", "StardewValley.Farmer.set__money",
        "StardewValley.Farmer.addUnearnedMoney", "StardewValley.Farmer.changeWalletTypeTonight",
        "StardewValley.FarmerTeam.GetMoney", "StardewValley.FarmerTeam.GetIndividualMoney",
        "StardewValley.FarmerTeam.AddIndividualMoney", "StardewValley.FarmerTeam.SetIndividualMoney",
        "StardewValley.FarmerTeam.money", "StardewValley.FarmerTeam.individualMoney", "StardewValley.FarmerTeam.useSeparateWallets",
        "StardewValley.Menus.ShopMenu.getPlayerCurrencyAmount", "StardewValley.Menus.ShopMenu.chargePlayer"
    };

    internal sealed record Site(string Caller, int Offset, string Target);

    internal static IReadOnlyList<Site> Read(string path)
    {
        using var dll = File.OpenRead(path);
        using var pe = new PEReader(dll);
        var metadata = pe.GetMetadataReader();
        var sites = new List<Site>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
                continue;
            string caller = Member(metadata, method.GetDeclaringType(), method.Name);
            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()
                ?? throw new BadImageFormatException($"Missing IL for {caller}.");
            for (int offset = 0; offset < il.Length;)
            {
                int address = offset;
                ushort opcode = il[offset++];
                if (opcode == 0xfe)
                    opcode = (ushort)(0xfe00 | il[offset++]);
                var operand = OperandTypes[opcode];
                if (operand is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok)
                {
                    var reference = MetadataTokens.EntityHandle(BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset)));
                    if (Resolve(metadata, reference) is { } target && Targets.Contains(target))
                        sites.Add(new(caller, address, target));
                }
                offset += OperandSize(operand, il.AsSpan(offset));
            }
        }
        return sites;
    }

    private static string? Resolve(MetadataReader metadata, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                return Member(metadata, method.GetDeclaringType(), method.Name);
            case HandleKind.FieldDefinition:
                var field = metadata.GetFieldDefinition((FieldDefinitionHandle)handle);
                return Member(metadata, field.GetDeclaringType(), field.Name);
            case HandleKind.MemberReference:
                var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                return Member(metadata, member.Parent, member.Name);
            case HandleKind.MethodSpecification:
                var specification = metadata.GetMethodSpecification((MethodSpecificationHandle)handle);
                return Resolve(metadata, specification.Method);
            default:
                return null;
        }
    }

    private static string Member(MetadataReader metadata, EntityHandle type, StringHandle name) =>
        TypeName(metadata, type) + "." + metadata.GetString(name);

    private static string TypeName(MetadataReader metadata, EntityHandle type)
    {
        if (type.Kind == HandleKind.TypeDefinition)
        {
            var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)type);
            return definition.IsNested
                ? TypeName(metadata, definition.GetDeclaringType()) + "+" + metadata.GetString(definition.Name)
                : metadata.GetString(definition.Namespace) + "." + metadata.GetString(definition.Name);
        }
        if (type.Kind == HandleKind.TypeReference)
        {
            var reference = metadata.GetTypeReference((TypeReferenceHandle)type);
            return reference.ResolutionScope.Kind == HandleKind.TypeReference
                ? TypeName(metadata, reference.ResolutionScope) + "+" + metadata.GetString(reference.Name)
                : metadata.GetString(reference.Namespace) + "." + metadata.GetString(reference.Name);
        }
        return string.Empty;
    }

    private static int OperandSize(OperandType operand, ReadOnlySpan<byte> bytes) => operand switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or
            OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or
            OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => checked(4 * (1 + BinaryPrimitives.ReadInt32LittleEndian(bytes))),
        _ => throw new BadImageFormatException($"Unsupported IL operand {operand}.")
    };
}
