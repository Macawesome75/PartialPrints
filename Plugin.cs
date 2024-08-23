using System;
using System.Collections.Generic;
using m75partialprints;
using BepInEx;
using HarmonyLib;
using Il2CppSystem.Text;
using BepInEx.Configuration;
using SOD.Common.Helpers;
using UnityEngine;
using SOD.Common;
using System.Reflection;
using SOD.Common.BepInEx;
using SOD.Common.Extensions;
using System.Linq;

namespace partialprints;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency(SOD.Common.Plugin.PLUGIN_GUID)]
public class PartialPrints : PluginController<PartialPrints>
{
    private static ConfigEntryCache<int> ConfigCodeLength;
    private static ConfigEntryCache<int> ConfigLettersPerDigit;
    private static ConfigEntryCache<int> ConfigSmudgeMin;
    private static ConfigEntryCache<int> ConfigSmudgeMax;

    // We want to remove fingerprint from match presets so that fingerprints don't match each other. We keep track of which MatchPresets we've already looked at to avoid additional work.
    public static readonly HashSet<MatchPreset> ProcessedMatchPresets = new HashSet<MatchPreset>();

    // We cache off any full prints we make so subsequent requests are almost instant. This gets cleared between game loads.
    private static readonly Il2CppSystem.Collections.Generic.Dictionary<uint, string> _fullPrintCache = new Il2CppSystem.Collections.Generic.Dictionary<uint, string>();

    // For partial prints, we put all possible indices in original and shuffle them into shuffled, then take from the front of shuffled to get our smudged indices. No need for allocation.
    private static int[] _partialIndicesOriginal;
    private static int[] _partialIndicesShuffled;

    // Even though we cache full prints to a dictionary, sometimes the game makes lots of repeated requests for the same one, and we can avoid that dictionary lookup here.
    private static string _lastFullPrint;
    private static uint _lastFullCitizen;

    // Same thing for partial prints, sometimes it requests the same one 50-100 times in a single frame.
    private static string _lastPartialPrint;
    private static uint _lastPartialCitizen;
    private static string _lastPartialEvidence;

    // We don't want to remake StringBuilders all the time, keep one statically and Clear it as needed.
    private static readonly StringBuilder _builder = new StringBuilder();

    // The city seed only changes once per load, so we'll cache it after loading to avoid needing to hash it repeatedly.
    private static uint _citySeedCache;

    public override void Load()
    {
        InitializeConfig();

        Harmony.PatchAll(Assembly.GetExecutingAssembly());
        Log.LogInfo("Plugin is patched.");

        Lib.SaveGame.OnAfterNewGame -= OnAfterNewGame;
        Lib.SaveGame.OnAfterNewGame += OnAfterNewGame;
        
        Lib.SaveGame.OnAfterLoad -= OnAfterLoad;
        Lib.SaveGame.OnAfterLoad += OnAfterLoad;

        InitializeStructures();
    }

    public override bool Unload()
    {
        Lib.SaveGame.OnAfterNewGame -= OnAfterNewGame;
        Lib.SaveGame.OnAfterLoad -= OnAfterLoad;
        return base.Unload();
    }

    private void OnAfterNewGame(object sender, EventArgs e)
    {
        Reinitialize();
    }
    
    private void OnAfterLoad(object save, SaveGameArgs args)
    {
        Reinitialize();
    }

    private void InitializeConfig()
    {
        ConfigCodeLength = new ConfigEntryCache<int>(Config, "General", "Code Length", 5, "How long each fingerprint code is, in characters.\nNOTE: There is no logic to prevent duplicate codes! If the code is too short there is a chance of duplicates.");
        ConfigLettersPerDigit = new ConfigEntryCache<int>(Config, "General", "Letters Per Digit", 26, "How many letters of the alphabet the fingerprint codes can use. Default uses all 26 letters, but decreasing the amount can add some ambiguity to prints.\nFor example, setting it to 10 would limit the codes to letters to A to J. Increasing this above 26 may cause odd behaviour.\nNOTE: As stated above, duplicate codes can exist. If you decrease this, it is recommended to increase the codes length.");
        ConfigSmudgeMin = new ConfigEntryCache<int>(Config, "General", "Minimum Smudge Amount", 2, "The minimum amount of \"smudged\" letters on partial fingerprints");
        ConfigSmudgeMax = new ConfigEntryCache<int>(Config, "General", "Maximum Smudge Amount", 4, "The maximum amount of \"smudged\" letters on partial fingerprints");

        ConfigSmudgeMin.Value = Mathf.Clamp(ConfigSmudgeMin.Value, 0, ConfigCodeLength.Value);
        ConfigSmudgeMax.Value = Mathf.Clamp(ConfigSmudgeMax.Value, 0, ConfigCodeLength.Value);

        if (ConfigSmudgeMin.Value <= ConfigSmudgeMax.Value)
        {
            return;
        }

        int temp = ConfigSmudgeMin.Value;
        ConfigSmudgeMin.Value = ConfigSmudgeMax.Value;
        ConfigSmudgeMax.Value = temp;
    }

    private void InitializeStructures()
    {
        // Initialize our structures for processing partials and MatchPresets. The important thing here is unique increasing indices in original, shuffled will be overwritten later, we're just giving it defaults.
        int length = ConfigCodeLength.Value;
        _partialIndicesOriginal = new int[length];
        _partialIndicesShuffled = new int[length];

        for (int i = 0; i < length; ++i)
        {
            _partialIndicesOriginal[i] = i;
            _partialIndicesShuffled[i] = i;
        }

        ProcessedMatchPresets.Clear();
    }

    private void Reinitialize()
    {
        // Make sure to clear our full print cache on load, so we're not carrying a bunch of citizens in memory from another city.
        _fullPrintCache.Clear();

        // Cache the city seed so we don't need to hash it all the time.
        _citySeedCache = GetDeterministicStringHash(CityData.Instance.seed);
    }

    #region Core Methods

    private static uint GetDeterministicStringHash(string s)
    {
        return Lib.SaveGame.GetUniqueNumber(s);
    }

    private static char GetPrintCharacter(uint citizenIndex, uint letterIndex)
    {
        // We're going to use XORShift here to avoid allocating a System.Random every time we need a letter. It's basically just bitwise math and prime numbers that create random looking numbers.
        const uint PRIME_1 = 2654435761;
        const uint PRIME_2 = 1629267613;
        const uint PRIME_3 = 334214467;
        const char FIRST_LETTER = 'A';

        // Hash the numbers together using bitwise stuff and XOR rather than instantiating random number generators, for speed.
        uint hash = _citySeedCache;
        hash ^= hash << 13;
        hash ^= hash >> 17;
        hash ^= hash << 5;

        hash += PRIME_1 * citizenIndex;
        hash ^= hash << 13;
        hash ^= hash >> 17;
        hash ^= hash << 5;

        hash += PRIME_2 * letterIndex;
        hash ^= hash << 13;
        hash ^= hash >> 17;
        hash ^= hash << 5;

        hash *= PRIME_3;

        // Map the hash to the A-Z range.
        return (char)(FIRST_LETTER + (hash % ConfigLettersPerDigit.Value));
    }

    /// <summary>
    /// Shuffles our indices and returns how many we should smudge.
    /// </summary>
    private static int ShufflePartialIndices(uint citizenIndex, string evidenceID)
    {
        // Hash together all of our stuff again to be used as a seed for the shuffle. Use different primes though.
        const uint PRIME_1 = 4294967291;
        const uint PRIME_2 = 2654435769;
        const uint PRIME_3 = 2166136261;

        // Hash the numbers together using bitwise stuff and XOR rather than instantiating random number generators, for speed.
        uint hash = _citySeedCache;
        hash ^= hash << 13;
        hash ^= hash >> 17;
        hash ^= hash << 5;

        hash += PRIME_1 * citizenIndex;
        hash ^= hash << 13;
        hash ^= hash >> 17;
        hash ^= hash << 5;

        hash += PRIME_2 * GetDeterministicStringHash(evidenceID);
        hash ^= hash << 13;
        hash ^= hash >> 17;
        hash ^= hash << 5;

        // Calculate the number of indices to smudge, deterministically.
        int smudgeCount = (int)(hash % (ConfigSmudgeMax.Value - ConfigSmudgeMin.Value + 1)) + ConfigSmudgeMin.Value;

        // We maintain the original so we can do this, the shuffle has to start from a predetermined point.
        Array.Copy(_partialIndicesOriginal, _partialIndicesShuffled, ConfigCodeLength.Value);

        // Perform a deterministic "Fisher-Yates" shuffle based on the seed.
        for (int i = _partialIndicesShuffled.Length - 1; i > 0; i--)
        {
            hash *= PRIME_3;
            hash ^= hash << 13;
            hash ^= hash >> 17;
            hash ^= hash << 5;

            int j = (int)(hash % (i + 1));

            // Swap i and j.
            int temp = _partialIndicesShuffled[i];
            _partialIndicesShuffled[i] = _partialIndicesShuffled[j];
            _partialIndicesShuffled[j] = temp;
        }

        // While we were XOR shifting we also calculated this to return for ApplyPartialDashes. How many letters to smudge.
        return smudgeCount;
    }

    private static string ApplyPartialDashes(string fingerprint, int dashCount)
    {
        const char SMUDGE_CHAR = '-';

        _builder.Clear();
        _builder.Append(fingerprint);

        for (int i = 0; i < dashCount; i++)
        {
            _builder[_partialIndicesShuffled[i]] = SMUDGE_CHAR;
        }

        return _builder.ToString();
    }

    public static string GetPrintFull(uint citizenIndex)
    {
        // First see if the game is DDOSing us with requests for the same citizen repeatedly. If so, avoid a dictionary lookup.
        if (citizenIndex == _lastFullCitizen)
        {
            return _lastFullPrint;
        }

        _lastFullCitizen = citizenIndex;

        // Now see if this is a citizen we've ever seen in this load, if so, return the cache.
        if (_fullPrintCache.TryGetValue(citizenIndex, out string fullPrint))
        {
            _lastFullPrint = fullPrint;
            return fullPrint;
        }

        // Okay we've never seen this person before, we need to build a print and cache it off for future requests.
        _builder.Clear();

        for (int i = 0; i < ConfigCodeLength.Value; i++)
        {
            _builder.Append(GetPrintCharacter(citizenIndex, (uint)i));
        }

        fullPrint = _builder.ToString();
        _fullPrintCache[citizenIndex] = fullPrint;
        _lastFullPrint = fullPrint;

        return fullPrint;
    }

    public static string GetPrintPartial(uint citizenIndex, string evidenceID)
    {
        if (citizenIndex == _lastPartialCitizen && evidenceID == _lastPartialEvidence)
        {
            return _lastPartialPrint;
        }

        _lastPartialCitizen = citizenIndex;
        _lastPartialEvidence = evidenceID;

        // This is non-deterministic on un-scanned prints across loads. evidenceID will change unless the print was scanned prior to saving.
        // So if you load and scan an un-scanned print two different times, it will have different smudges. The citizen ID won't change though so the full print will be the same.
        string fullPrint = GetPrintFull(citizenIndex);
        int amountToSmudge = ShufflePartialIndices(citizenIndex, evidenceID);
        string partialPrint = ApplyPartialDashes(fullPrint, amountToSmudge);

        _lastPartialPrint = partialPrint;
        return partialPrint;
    }

    #endregion
}

#region Patch: Evidence.AddFactLink

[HarmonyPatch(typeof(Evidence), nameof(Evidence.AddFactLink), new[]
{
typeof(Fact),
typeof(Evidence.DataKey),
typeof(bool)
})]
internal class Evidence_AddFactLinkSingle
{
    [HarmonyPrefix]
    internal static bool Prefix(Fact newFact, Evidence.DataKey newKey)
    {
        // Stops "FingerprintBelongsTo" facts from being linked to fingerprint evidence notes
        if (newKey == Evidence.DataKey.fingerprints && newFact.preset.name.Equals("FingerprintBelongsTo"))
            return false;
        return true;
    }
}

[HarmonyPatch(typeof(Evidence), nameof(Evidence.AddFactLink), new[]
{
typeof(Fact),
typeof(Il2CppSystem.Collections.Generic.List<Evidence.DataKey>),
typeof(bool)
})]
internal class Evidence_AddFactLinkMulti
{
    [HarmonyPrefix]
    internal static bool Prefix(Fact newFact, Il2CppSystem.Collections.Generic.List<Evidence.DataKey> newKey)
    {
        // Stops "FingerprintBelongsTo" facts from being linked to fingerprint evidence notes
        if (newFact.preset.name.Equals("FingerprintBelongsTo") && newKey.AsEnumerable().Any(a => a == Evidence.DataKey.fingerprints))
            return false;
        return true;
    }
}

#endregion

#region Patch: EvidenceFingerprint.GetNote

[HarmonyPatch(typeof(EvidenceFingerprint), "GetNote")]
public class M75GetNotePatch
{
    private static readonly StringBuilder _noteBuilder = new StringBuilder();

    private const string SPRITE_EMPTY = "<sprite=\"icons\" name=\"Checkbox Empty\">";
    private const string SPRITE_CHECKED = "<sprite=\"icons\" name=\"Checkbox Checked\">";
    private const string PREFIX_FONT = "<font=\"PapaManAOE SDF\">";
    private const string SUFFIX_FONT = "</font>";

    public static bool Prefix(ref string __result, EvidenceFingerprint __instance, Il2CppSystem.Collections.Generic.List<Evidence.DataKey> keys)
    {
        _noteBuilder.Clear();
        Il2CppSystem.Collections.Generic.List<Evidence.DataKey> tiedKeys = __instance.GetTiedKeys(keys);

        string checkbox = !tiedKeys.Contains(Evidence.DataKey.fingerprints) ? SPRITE_EMPTY : SPRITE_CHECKED;

        _noteBuilder.Append(checkbox);
        _noteBuilder.Append("Fingerprint");
        _noteBuilder.Append(": ");
        _noteBuilder.Append(PREFIX_FONT);

        if (tiedKeys.Contains(Evidence.DataKey.fingerprints) && __instance.writer != null)
        {
            _noteBuilder.Append(PartialPrints.GetPrintPartial((uint)__instance.writer.humanID, __instance.evID));

            _noteBuilder.Append(SUFFIX_FONT);
            _noteBuilder.Append(Environment.NewLine);
            _noteBuilder.Append(SPRITE_CHECKED);
            _noteBuilder.Append($"Description: {PREFIX_FONT}A partial fingerprint.");
            _noteBuilder.Append(SUFFIX_FONT);
        }
        else
        {
            _noteBuilder.Append(Strings.Get("descriptors", "?"));
        }

        __result = _noteBuilder.ToString();
        return false;
    }
}

#endregion

#region Patch: EvidenceFingerprint.GetNameForDataKey

[HarmonyPatch(typeof(EvidenceFingerprint), "GetNameForDataKey")]
public class M75GetNameForDataKeyPatch
{
    public static string Postfix(string __result, EvidenceFingerprint __instance, Il2CppSystem.Collections.Generic.List<Evidence.DataKey> inputKeys)
    {
        // Fingerprint evidence notes are always partial, this is the title of the window
        return $"Fingerprint ({PartialPrints.GetPrintPartial((uint)__instance.writer.humanID, __instance.evID)})";
    }
}

#endregion

#region Patch: EvidenceFingerprintController.CheckEnabled

[HarmonyPatch(typeof(EvidenceFingerprintController), "CheckEnabled")]
public class M75EvidenceFingerprintControllerPatch
{
    public static void Postfix(EvidenceFingerprintController __instance)
    {
        if (__instance.parentWindow.evidenceKeys.Contains(Evidence.DataKey.fingerprints))
        {
            // Show the full print in the human evidence window if we have fingerprints unlocked
            // Fingerprints cannot be unlocked using the scanner (this returns only partial prints)
            // Fingerprints can be unlocked by taking prints of the body, through the government database, or possibly through a side job?
            __instance.identifierText.text = $"ID: {PartialPrints.GetPrintFull((uint)__instance.parentWindow.passedEvidence.writer.humanID)}";
        }
    }
}

#endregion

#region Patch: GameplayController.AddNewMatch

[HarmonyPatch(typeof(GameplayController), "AddNewMatch")]
public class M75FactMatchesPatch
{
    // Makes it so that fingerprints don't auto connect facts.
    public static bool Prefix(MatchPreset match, Evidence newEntry)
    {
        // If we've already processed it, ignore it.
        if (PartialPrints.ProcessedMatchPresets.Contains(match))
        {
            return true;
        }

        for (int i = match.matchConditions.Count - 1; i >= 0; --i)
        {
            if (match.matchConditions._items[i] == MatchPreset.MatchCondition.fingerprint)
            {
                // We technically want to remove this, but the default if no conditions are present is to match. So instead, we change it to visualDescriptors.
                // This will attempt to cast the EvidenceFingerprint to an EvidenceCitizen, fail, and return false...which is our end goal. It's a roundabout way to what we want.
                match.matchConditions._items[i] = MatchPreset.MatchCondition.visualDescriptors;
                //SOD.Common.Plugin.Log.LogInfo("Visualdescriptor");
            }
        }

        // Keep track of what we've processed.
        PartialPrints.ProcessedMatchPresets.Add(match);
        return true;
    }
}

#endregion

#region Additional Structures

public class ConfigEntryCache<T>
{
    // ConfigEntry is doing some weird stuff when you access Value that I don't trust, so here we mainly just cache Value to a simple field, not a property. So we know it isn't being weird.
    public T Value;

    public ConfigEntryCache(ConfigFile file, string section, string key, T defaultValue, string description)
    {
        ConfigEntry<T> entry = file.Bind(section, key, defaultValue, description);
        Value = entry.Value;
    }
}

#endregion
