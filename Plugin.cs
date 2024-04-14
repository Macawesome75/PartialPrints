using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;
using Il2CppSystem.Text;
using m75partialprints;
using Rewired.Demos;
using UnityEngine;
using Il2CppInterop.Generator.Extensions;
using UnityEngine.Rendering;
using BepInEx.Configuration;

namespace partialprints;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class PartialPrints : BasePlugin
{
    public static ConfigEntry<int> configCodeLength;
    public static ConfigEntry<int> configAmountToShow;
    public static ConfigEntry<int> configLettersPerDigit;
    public static ConfigEntry<bool> configShowOwnerOnCard;
    public override void Load()
    {
        configCodeLength = Config.Bind("General", "codeLength", 5, "How long each fingerprint code is, in characters.\nNOTE: There is no logic to prevent duplicate codes! If the code is too short there is a chance of duplicates.");
        configAmountToShow = Config.Bind("General", "amountToShow", 2, "How many letters of the code are shown per partial fingerprint");
        configLettersPerDigit = Config.Bind("General", "lettersPerDigit", 26, "How many letters of the alphabet the fingerprint codes can use. Default uses all 26 letters, but decreasing the amount can add some ambiguity to prints.\nFor example, setting it to 10 would limit the codes to letters to A to J. Increasing this above 26 may cause odd behaviour.\nNOTE: As stated above, duplicate codes can exist. If you decrease this, it is recommended to increase the codes length.");

        //configShowOwnerOnCard = Config.Bind("Information", "showOwnerOnCard", true, "Whether or not the evidence card shows the fingerprint's owner.\nRecommended to turn off if you want the difficulty");

        // Plugin startup logic
        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        var harmony = new Harmony($"{MyPluginInfo.PLUGIN_GUID}");
        harmony.PatchAll();
    }

    public static string PartialPrintCode(string ownerseed, string evID)
    {
        int amountToShow = configAmountToShow.Value;
        int codeLength = configCodeLength.Value;
        int amountToHide = codeLength - amountToShow;
        if (amountToHide > codeLength) { amountToHide = codeLength; }
        StringBuilder printCode = CreateFullPrintCode(ownerseed);
        int failSafe = 10;
        for (int i = 0; i < amountToHide; i++)
        {
            string base26i = Toolbox.Instance.ToBase26(i); //Changing i to base 26 seems to fix some weird RNG issue
            int indexCode = Toolbox.Instance.GetPsuedoRandomNumber(0, codeLength-1, evID + base26i, true); //evID seems stable enough to use as a seed
            //SOD.Common.Plugin.Log.LogInfo(evID + base26i);
            while (printCode[indexCode] == '-' && failSafe > 0)
            {
                indexCode--;
                if (indexCode < 0) { indexCode = codeLength-1; }
                failSafe--;
            }
            printCode[indexCode] = '-';
        }
        return printCode.ToString();
    }

    public static StringBuilder CreateFullPrintCode(string ownerseed)
    {
        int codeLength = configCodeLength.Value;
        int lettersPerDigit = configLettersPerDigit.Value;
        StringBuilder stringBuilder = new StringBuilder();
        string printcode = "";
        for (int i = 0; i < codeLength; i++)
        {
            int letter = Toolbox.Instance.GetPsuedoRandomNumber(1, lettersPerDigit, ownerseed + i);
            stringBuilder.Append(printcode + Toolbox.Instance.ToBase26(letter));
        }
        return stringBuilder;
    }
}

[HarmonyPatch(typeof(EvidenceFingerprint),"GetNote")] //For the body of the evidence card
public class M75_GetNote_Patch
{
    public static string Postfix(string __result, EvidenceFingerprint __instance, List<Evidence.DataKey> keys)
    {
        StringBuilder stringBuilder = new StringBuilder();
        stringBuilder.Append(__result);
        List<Evidence.DataKey> tiedKeys = __instance.GetTiedKeys(keys);
        if (tiedKeys.Contains(Evidence.DataKey.fingerprints) && __instance.writer != null)
        {
            //SOD.Common.Plugin.Log.LogInfo("Trying to replace");
            string ownerseed = __instance.writer.GetCitizenName() + CityData.Instance.seed; //Citizen Name + Seed = Fingerprint code
            int loop = __instance.writer.fingerprintLoop;
            string loop26 = Toolbox.Instance.ToBase26(loop); //What am i doing
            int loopIndex = __result.IndexOf('>'+loop26+'<'); //Get the position of the fingerprint loop in the full string
            string partialcode = PartialPrints.PartialPrintCode(ownerseed, __instance.evID);
            stringBuilder.Replace(loop26, partialcode, loopIndex + 1, loop26.Length + 1);
            /*
            if (!PartialPrints.configShowOwnerOnCard.Value) //Trying to remove the owner from the evidence card
            {
                string 
                int newlineIndex = __result.LastIndexOf("\n");
                __result.Remove(newlineIndex); //Just remove everything after the newline, what could go wrong?
            }    
            */
            //SOD.Common.Plugin.Log.LogInfo("Trying to replace" + loop26 + "with" + partialcode + "In" + __result + "Colonindex" + loopIndex);
        }
        //SOD.Common.Plugin.Log.LogInfo(stringBuilder.ToString());
        return stringBuilder.ToString();
    }
}

[HarmonyPatch(typeof(EvidenceFingerprint), "GetNameForDataKey")]
public class M75_GetNameForDataKey_Patch
{
    public static string Postfix(string __result, EvidenceFingerprint __instance, List<Evidence.DataKey> inputKeys) //Probably should be a transpiler, but I don't know how to do IL stuff yet.
    {
        string com = __result;
        if (__instance.customNames.Count > 0 || inputKeys == null) { return com; }
        string ownerseed = __instance.writer.GetCitizenName() + CityData.Instance.seed; //Citizen Name + Seed = Fingerprint code
        com = "ID: " + PartialPrints.PartialPrintCode(ownerseed, __instance.evID);
        return com;
    }
}
[HarmonyPatch(typeof(EvidenceFingerprintController), "CheckEnabled")]
public class M75_EvidenceFingerprintController_Patch
{
    public static void Postfix(EvidenceFingerprintController __instance)
    {
        if (__instance.parentWindow.evidenceKeys.Contains(Evidence.DataKey.fingerprints))
        {
            string ownerseed = __instance.parentWindow.passedEvidence.writer.GetCitizenName() + CityData.Instance.seed;
            __instance.identifierText.text = Strings.Get("evidence.generic", "Type", Strings.Casing.firstLetterCaptial, false, false, false, null) + " " + PartialPrints.CreateFullPrintCode(ownerseed).ToString();
        }
    }
}

[HarmonyPatch(typeof(FactMatches), "MatchCheck")]
public class M75_FactMatches_Patch
{
    public static bool Postfix(bool __result, MatchPreset match)
    {
        foreach (MatchPreset.MatchCondition matchCondition in match.matchConditions)
        {
            if (matchCondition == MatchPreset.MatchCondition.fingerprint) //Makes it so that fingerprints don't auto connect facts
            {
                return false;
            }
        }
        return __result;
    }
}