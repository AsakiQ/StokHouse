namespace StokHouse;

internal static class RobinDialogue
{
    internal const string Choice = "StokHouseUpgrade";
    internal const string SharedAnswer = "carpenter_" + Choice;
    internal const string VanillaAnswer = "carpenter_Upgrade";

    internal static bool IsUpgradeAnswer(string? key) => key is SharedAnswer or VanillaAnswer;

    internal static bool TryHandle(string? key, string? refusal, Action showQuote, Action<string> showRefusal)
    {
        if (!IsUpgradeAnswer(key) || (key == VanillaAnswer && refusal is not null))
            return false;
        if (refusal is null)
            showQuote();
        else
            showRefusal(refusal);
        return true;
    }
}
