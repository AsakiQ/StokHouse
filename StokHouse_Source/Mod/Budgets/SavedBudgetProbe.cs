using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace StokHouse.FamilyBudget;

// SMAPI's Saved event signals the end of a save cycle. The main game data file
// must also contain this exact host snapshot before the new balance is exposed.
internal static class SavedBudgetProbe
{
    internal const string JournalKey = "StokHouse.CoopHousing/family-budget";

    internal static string? Fingerprint(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var digest = SHA256.Create();
            return Convert.ToHexString(digest.ComputeHash(file));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static bool HasWallets(string file, ulong farmIdentity, IReadOnlyDictionary<long, int> expected)
    {
        if (expected.Count == 0)
            return true;
        var reading = new Dictionary<long, int>();
        ulong? identity = null;
        try
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var xml = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 256 * 1024 * 1024
            });
            if (xml.MoveToContent() != XmlNodeType.Element || xml.Name != "SaveGame")
                return false;
            bool farmhands = false;
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == 1 && xml.Name == "farmhands")
                    farmhands = false;
                if (xml.NodeType != XmlNodeType.Element)
                    continue;
                if (xml.Depth == 1 && xml.Name == "uniqueIDForThisGame")
                {
                    if (identity is not null || !ulong.TryParse(ReadElement(xml).Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out ulong id))
                        return false;
                    identity = id;
                }
                else if (xml.Depth == 1 && xml.Name == "farmhands" && !xml.IsEmptyElement)
                    farmhands = true;
                else if ((xml.Depth == 1 && xml.Name == "player") || (farmhands && xml.Depth == 2 && xml.Name == "Farmer"))
                {
                    if (!ReadWallet(xml, out long farmer, out int balance) || !reading.TryAdd(farmer, balance))
                        return false;
                }
            }
            return identity == farmIdentity && expected.All(pair => reading.TryGetValue(pair.Key, out int balance) && balance == pair.Value);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (XmlException) { return false; }
    }

    private static bool ReadWallet(XmlReader xml, out long id, out int money)
    {
        id = 0;
        money = -1;
        using var farmer = xml.ReadSubtree();
        while (farmer.Read())
        {
            if (farmer.NodeType != XmlNodeType.Element || farmer.Depth != 1)
                continue;
            if (farmer.Name == "UniqueMultiplayerID" && (id != 0 || !long.TryParse(ReadElement(farmer).Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out id)))
                return false;
            if (farmer.Name == "money" && (money != -1 || !int.TryParse(ReadElement(farmer).Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out money)))
                return false;
        }
        return id != 0 && money >= 0;
    }

    internal static DiskState Inspect(string file, ulong farmId, string staged, string? previous, bool saveEnded)
    {
        if (!saveEnded || farmId == 0 || string.IsNullOrEmpty(staged) || staged == previous)
            return DiskState.Unknown;
        try
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var xml = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 256 * 1024 * 1024
            });
            if (xml.MoveToContent() != XmlNodeType.Element || xml.Name != "SaveGame" || xml.IsEmptyElement)
                return DiskState.Unknown;

            bool sawHost = false;
            ulong? identity = null;
            string? journal = null;
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element || xml.Depth != 1)
                    continue;
                if (xml.Name == "uniqueIDForThisGame")
                {
                    if (identity is not null || !ulong.TryParse(ReadElement(xml).Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out ulong id))
                        return DiskState.Unknown;
                    identity = id;
                }
                else if (xml.Name == "player")
                {
                    if (sawHost)
                        return DiskState.Unknown;
                    sawHost = true;
                    if (!ReadHostJournal(xml, out journal))
                        return DiskState.Unknown;
                }
            }

            if (!sawHost || identity != farmId)
                return DiskState.Unknown;
            if (journal == staged)
                return DiskState.Written;

            // Never undo in-memory wallets if the file is missing, corrupt, unfamiliar,
            // or contains another checkpoint that might have superseded our attempt.
            return journal == previous ? DiskState.NotWritten : DiskState.Unknown;
        }
        catch (IOException) { return DiskState.Unknown; }
        catch (UnauthorizedAccessException) { return DiskState.Unknown; }
        catch (XmlException) { return DiskState.Unknown; }
    }

    private static bool ReadHostJournal(XmlReader xml, out string? journal)
    {
        journal = null;
        bool found = false;
        using var host = xml.ReadSubtree();
        while (host.Read())
        {
            if (host.NodeType != XmlNodeType.Element || host.Depth != 1 || host.Name != "modData")
                continue;
            if (found)
                return false;
            found = true;
            var data = ReadElement(host);
            // NetDictionary exposes a serializable dictionary to XmlSerializer. Its XML
            // is an item/key/value sequence inside the serializer's dictionary element.
            var elements = data.Elements().ToArray();
            if (elements.Length == 1 && elements[0].Name.LocalName == "SerializableDictionaryOfStringString")
                elements = elements[0].Elements().ToArray();
            foreach (var entry in elements)
            {
                if (entry.Name != "item" || entry.Element("key")?.Element("string") is not { } key ||
                    entry.Element("value")?.Element("string") is not { } value)
                    return false;
                if (key.Value != JournalKey)
                    continue;
                if (journal is not null || string.IsNullOrEmpty(value.Value))
                    return false;
                journal = value.Value;
            }
        }
        return true;
    }

    private static XElement ReadElement(XmlReader reader)
    {
        using var child = reader.ReadSubtree();
        child.MoveToContent();
        return (XElement)XNode.ReadFrom(child);
    }
}
