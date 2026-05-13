using System.Collections.Generic;

namespace LetMeSoloThem.State;

internal static class SpareChassisInventory
{
    private static readonly Dictionary<string, int> _countsBySteamID = new();

    public static int Count(string steamID)
    {
        if (string.IsNullOrEmpty(steamID)) return 0;
        return _countsBySteamID.TryGetValue(steamID, out var c) ? c : 0;
    }

    public static bool Has(string steamID) => Count(steamID) > 0;

    public static void Set(string steamID, int count)
    {
        if (string.IsNullOrEmpty(steamID)) return;
        if (count <= 0)
        {
            _countsBySteamID.Remove(steamID);
        }
        else
        {
            _countsBySteamID[steamID] = count;
        }
    }

    public static bool Consume(string steamID)
    {
        if (string.IsNullOrEmpty(steamID)) return false;
        if (!_countsBySteamID.TryGetValue(steamID, out var c) || c <= 0) return false;
        c--;
        if (c <= 0)
            _countsBySteamID.Remove(steamID);
        else
            _countsBySteamID[steamID] = c;
        Plugin.Log.LogDebug($"[Revive] Spare Chassis consumed (steamID={steamID}, remaining={c})");
        return true;
    }
}
