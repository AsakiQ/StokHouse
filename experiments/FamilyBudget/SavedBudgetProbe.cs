using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace StokHouse.FamilyBudget.Spec;

// SMAPI's Saved event signals the end of a save cycle. The main game data file
// must also contain this exact host snapshot before the new balance is exposed.
internal static class SavedBudgetProbe
{
    internal const string JournalKey = "StokHouse.CoopHousing/family-budget";

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
