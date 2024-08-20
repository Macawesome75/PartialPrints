using System;
using System.Collections.Generic;
using m75partialprints;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppSystem.Text;
using BepInEx.Configuration;
using SOD.Common.Helpers;
using UnityEngine;

namespace partialprints;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class PartialPrints : BasePlugin
{
    private static ConfigEntryCache<int> ConfigCodeLength;
    private static ConfigEntryCache<int> ConfigLettersPerDigit;
    private static ConfigEntryCache<int> ConfigSmudgeMin;
    private static ConfigEntryCache<int> ConfigSmudgeMax;
    public static ConfigEntryCache<BelongsToMode> ConfigBelongsToMode;

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
        
        // Plugin startup logic.
        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        Harmony harmony = new Harmony($"{MyPluginInfo.PLUGIN_GUID}");
        harmony.PatchAll();

        SOD.Common.Lib.SaveGame.OnAfterLoad -= OnAfterLoad;
        SOD.Common.Lib.SaveGame.OnAfterLoad += OnAfterLoad;

        InitializeStructures();
    }

    public override bool Unload()
    {
        SOD.Common.Lib.SaveGame.OnAfterLoad -= OnAfterLoad;
        return base.Unload();
    }

    private void OnAfterLoad(object save, SaveGameArgs args)
    {
        ReinitializePostLoad();
    }
    
    private void InitializeConfig()
    {
        ConfigCodeLength = new ConfigEntryCache<int>(Config, "General", "Code Length", 5, "How long each fingerprint code is, in characters.\nNOTE: There is no logic to prevent duplicate codes! If the code is too short there is a chance of duplicates.");
        ConfigLettersPerDigit = new ConfigEntryCache<int>(Config, "General", "Letters Per Digit", 26, "How many letters of the alphabet the fingerprint codes can use. Default uses all 26 letters, but decreasing the amount can add some ambiguity to prints.\nFor example, setting it to 10 would limit the codes to letters to A to J. Increasing this above 26 may cause odd behaviour.\nNOTE: As stated above, duplicate codes can exist. If you decrease this, it is recommended to increase the codes length.");
        ConfigSmudgeMin = new ConfigEntryCache<int>(Config, "General", "Minimum Smudge Amount", 2, "The minimum amount of \"smudged\" letters on partial fingerprints");
        ConfigSmudgeMax = new ConfigEntryCache<int>(Config, "General", "Maximum Smudge Amount", 4, "The maximum amount of \"smudged\" letters on partial fingerprints");
        ConfigBelongsToMode = new ConfigEntryCache<BelongsToMode>(Config, "General", "Belongs To Mode", BelongsToMode.Hidden, "Determines what is done with the \"belongs to\" line on partial fingerprint notes.\n1. Hidden: The line is not shown at all. (Default)\n2. Obscured: The line is shown, but hides the person even if you know their print.\n3. Shown: If you know a person's full print, they will be shown on partial prints.");

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

    private void ReinitializePostLoad()
    {
        // Make sure to clear our full print cache on load, so we're not carrying a bunch of citizens in memory from another city.
        _fullPrintCache.Clear();
        
        // Cache the city seed so we don't need to hash it all the time.
        _citySeedCache = GetDeterministicStringHash(CityData.Instance.seed);
    }

    #region Core Methods
    
    private static uint GetDeterministicStringHash(string s)
    {
        // Fun fact, string.GetHashCode is deterministic within a game launch, but not ACROSS game launches. "Bob".GetHashCode will be different in one launch from another.
        // So we need to make this hashing method to be able to hash strings deterministically across program launches, or people's fingerprints will change.
        // Source: https://andrewlock.net/why-is-string-gethashcode-different-each-time-i-run-my-program-in-net-core/
        unchecked
        {
            uint hash1 = (5381 << 16) + 5381;
            uint hash2 = hash1;

            for (int i = 0; i < s.Length; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ s[i];
                
                if (i == s.Length - 1)
                {
                    break;
                }

                hash2 = ((hash2 << 5) + hash2) ^ s[i + 1];
            }

            return hash1 + (hash2 * 1566083941);
        }
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

#region Patch: EvidenceFingerprint.GetNote

[HarmonyPatch(typeof(EvidenceFingerprint),"GetNote")]
public class M75GetNotePatch
{
    // Don't use our builder in PartialPrints because we need it to make the print itself, so it will conflict.
    private static readonly StringBuilder _noteBuilder = new StringBuilder();
    
    private const string SPRITE_EMPTY = "<sprite=\"icons\" name=\"Checkbox Empty\">";
    private const string SPRITE_CHECKED = "<sprite=\"icons\" name=\"Checkbox Checked\">";
    private const string PREFIX_FONT = "<font=\"PapaManAOE SDF\">";
    private const string SUFFIX_FONT = "</font>";
    
    //For the body of the evidence card. This is basically the original code, overridden and heavily modified for our purposes. I also made it faster by unwrapping a lot of string concatenation.
    public static bool Prefix(ref string __result, EvidenceFingerprint __instance, Il2CppSystem.Collections.Generic.List<Evidence.DataKey> keys)
    {
        _noteBuilder.Clear();
        Il2CppSystem.Collections.Generic.List<Evidence.DataKey> tiedKeys = __instance.GetTiedKeys(keys);
        
        string checkbox = !tiedKeys.Contains(Evidence.DataKey.fingerprints) ? SPRITE_EMPTY : SPRITE_CHECKED;

        _noteBuilder.Append(checkbox);
        _noteBuilder.Append(Strings.Get("descriptors", "Type", Strings.Casing.firstLetterCaptial));
        _noteBuilder.Append(": ");
        _noteBuilder.Append(PREFIX_FONT);
        
        if (tiedKeys.Contains(Evidence.DataKey.fingerprints) && __instance.writer != null)
        {
            _noteBuilder.Append(PartialPrints.GetPrintPartial((uint)__instance.writer.humanID, __instance.evID));
        }
        else
        {
            _noteBuilder.Append(Strings.Get("descriptors", "?"));
        }

        _noteBuilder.Append(SUFFIX_FONT);

        switch (PartialPrints.ConfigBelongsToMode.Value)
        {
            case BelongsToMode.Shown:
                _noteBuilder.Append(Environment.NewLine);
                _noteBuilder.Append(SPRITE_EMPTY);
                _noteBuilder.Append(Strings.Get("descriptors", "Belongs To", Strings.Casing.firstLetterCaptial));
                _noteBuilder.Append(": ");
                _noteBuilder.Append(PREFIX_FONT);
                _noteBuilder.Append(tiedKeys.Contains(Evidence.DataKey.name) ? "|name|" : Strings.Get("descriptors", "?"));
                _noteBuilder.Append(SUFFIX_FONT);
                break;
            case BelongsToMode.Obscured:
                _noteBuilder.Append(Environment.NewLine);
                _noteBuilder.Append(SPRITE_EMPTY);
                _noteBuilder.Append(Strings.Get("descriptors", "Belongs To", Strings.Casing.firstLetterCaptial));
                _noteBuilder.Append(": ");
                _noteBuilder.Append(PREFIX_FONT);
                _noteBuilder.Append(Strings.Get("descriptors", "?"));
                _noteBuilder.Append(SUFFIX_FONT);
                break;
            case BelongsToMode.Hidden:
                // Do not append anything.
                break;
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
        //Probably should be a transpiler, but I don't know how to do IL stuff yet.
        if (__instance.customNames.Count <= 0 && inputKeys != null)
        {
            return $"ID: {PartialPrints.GetPrintPartial((uint)__instance.writer.humanID, __instance.evID)}";
        }

        return __result;
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
            __instance.identifierText.text = $"{Strings.Get("evidence.generic", "Type", Strings.Casing.firstLetterCaptial)} {PartialPrints.GetPrintFull((uint)__instance.parentWindow.passedEvidence.writer.humanID)}";
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

[HarmonyPatch(typeof(FactMatches), "MatchCheck")]
public class Bruh
{
    public static bool Postfix(bool __result, MatchPreset match)
    {
        foreach (MatchPreset.MatchCondition matchCondition in match.matchConditions)
        {
            if (matchCondition == MatchPreset.MatchCondition.fingerprint) //Makes it so that fingerprints don't auto connect facts
            {
                //SOD.Common.Plugin.Log.LogInfo("fingerprint wtf");
            }
            if (matchCondition == MatchPreset.MatchCondition.visualDescriptors)
            {
                //SOD.Common.Plugin.Log.LogInfo("visual yey");
            }
        }
        return __result;
    }
}

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

public enum BelongsToMode
{
    Hidden,
    Obscured,
    Shown,
}

#endregion