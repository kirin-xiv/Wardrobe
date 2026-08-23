using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vestiary.Services;

public class GlamourerService
{
    private readonly ICallGateSubscriber<Dictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor, bool ShownInQdb)>> designListSubscriber;
    private readonly ICallGateSubscriber<Guid, string?> designBase64Subscriber;
    private readonly ICallGateSubscriber<Guid, JObject?> designJObjectSubscriber;
    private readonly ICallGateSubscriber<Guid, int, uint, ulong, int> applyDesignSubscriber;
    private readonly ICallGateSubscriber<Guid, int> deleteDesignSubscriber;
    private readonly IPluginLog log;
    private readonly Configuration configuration;

    private Dictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor, bool ShownInQdb)>? cachedDesignList;
    private DateTime cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan DesignListCacheTtl = TimeSpan.FromSeconds(2);

    private readonly Dictionary<Guid, DateTime> designDateCache = new();
    private readonly Dictionary<Guid, List<string>> designTagsCache = new();

    // Tag cache refreshes are queued and processed in small per-frame batches
    // so the UI never blocks on hundreds of GetDesignJObject IPC calls at once.
    // Refresh cycles are started on demand (button click, editor open, or design
    // list changes) rather than by polling.
    private readonly Queue<Guid> tagRefreshQueue = new();
    private bool tagRefreshInProgress;
    private DateTime lastTagRefreshProcess = DateTime.MinValue;
    private static readonly TimeSpan TagRefreshProcessThrottle = TimeSpan.FromMilliseconds(16);
    private const int MaxTagRefreshesPerProcess = 40;

    public GlamourerService(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog, Configuration configuration)
    {
        log = pluginLog;
        this.configuration = configuration;
        
        designListSubscriber = pluginInterface.GetIpcSubscriber<Dictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor, bool ShownInQdb)>>(
            "Glamourer.GetDesignListExtended");

        designBase64Subscriber = pluginInterface.GetIpcSubscriber<Guid, string?>(
            "Glamourer.GetDesignBase64");

        designJObjectSubscriber = pluginInterface.GetIpcSubscriber<Guid, JObject?>(
            "Glamourer.GetDesignJObject");
        
        applyDesignSubscriber = pluginInterface.GetIpcSubscriber<Guid, int, uint, ulong, int>(
            "Glamourer.ApplyDesign");
        
        deleteDesignSubscriber = pluginInterface.GetIpcSubscriber<Guid, int>(
            "Glamourer.DeleteDesign");
    }

    /// <summary>
    /// Get the full design list from Glamourer. Results are cached for 2 seconds
    /// to avoid redundant IPC calls every frame. The cache is invalidated immediately
    /// when a design is applied or deleted.
    /// </summary>
    public Dictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor, bool ShownInQdb)> GetDesignList()
    {
        if (cachedDesignList != null && DateTime.UtcNow < cacheExpiry)
            return cachedDesignList;

        var previous = cachedDesignList;
        cachedDesignList = designListSubscriber.InvokeFunc();
        cacheExpiry = DateTime.UtcNow + DesignListCacheTtl;

        if (cachedDesignList == null)
        {
            // Glamourer unavailable: drop cached data so we don't serve stale results.
            designDateCache.Clear();
            designTagsCache.Clear();
            tagRefreshQueue.Clear();
            tagRefreshInProgress = false;
            return cachedDesignList;
        }

        // Designs were added or removed: drop cached dates so newly-created designs
        // get fresh timestamps, and refresh the tag cache (pruning deleted designs
        // while keeping existing tags visible so the gallery doesn't blink).
        bool keysChanged = previous != null && !KeysMatch(previous, cachedDesignList);
        if (keysChanged)
        {
            designDateCache.Clear();
            RequestTagRefresh(clearCache: true);
        }
        else if (previous == null)
        {
            // The cache was invalidated without a prior snapshot (e.g., after applying
            // a design). Keys are expected to be unchanged, but drop tag entries for
            // designs that no longer exist so they can't linger in memory.
            foreach (var stale in designTagsCache.Keys
                         .Where(k => !cachedDesignList.ContainsKey(k))
                         .ToList())
                designTagsCache.Remove(stale);
        }

        return cachedDesignList;
    }

    private static bool KeysMatch<T>(Dictionary<Guid, T> a, Dictionary<Guid, T> b)
    {
        if (a.Count != b.Count)
            return false;

        foreach (var key in a.Keys)
        {
            if (!b.ContainsKey(key))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Force the next GetDesignList call to fetch fresh data from Glamourer.
    /// </summary>
    /// <param name="clearTags">
    /// When true (design deleted) the tag cache is dropped too, since a design
    /// may be gone. Applying a design passes false — applying changes the player's
    /// glamour, not design metadata — so cached tags stay valid and a visible
    /// tag-based collection doesn't blink and rebuild on every apply.
    /// </param>
    private void InvalidateDesignListCache(bool clearTags = true)
    {
        cachedDesignList = null;
        cacheExpiry = DateTime.MinValue;
        designDateCache.Clear();

        if (!clearTags)
            return;

        designTagsCache.Clear();
        tagRefreshQueue.Clear();
        tagRefreshInProgress = false;
        lastTagRefreshProcess = DateTime.MinValue;
    }

    /// <summary>
    /// Queue designs for an incremental tag reload. Called by the manual refresh
    /// button, when opening the collection editor, and whenever the design list
    /// changes structurally.
    /// - <paramref name="clearCache"/> = true (design list changed): drop tags for
    ///   designs that no longer exist and queue only newly-added designs. Existing
    ///   cached tags stay visible so a visible collection doesn't blink.
    /// - <paramref name="clearCache"/> = false (manual refresh): queue every design
    ///   for background revalidation; stale tags stay visible until each design is
    ///   re-read.
    /// </summary>
    public void RequestTagRefresh(bool clearCache = false)
    {
        tagRefreshQueue.Clear();

        if (cachedDesignList != null)
        {
            if (clearCache)
            {
                // Drop tags for designs that no longer exist, but keep the rest
                // so the visible collection doesn't lose all its tag matches.
                foreach (var stale in designTagsCache.Keys
                             .Where(k => !cachedDesignList.ContainsKey(k))
                             .ToList())
                    designTagsCache.Remove(stale);
            }

            // clearCache=false: revalidate every design in the background.
            // clearCache=true:  only load the newly-added designs.
            foreach (var key in cachedDesignList.Keys)
            {
                if (!clearCache || !designTagsCache.ContainsKey(key))
                    tagRefreshQueue.Enqueue(key);
            }
        }

        tagRefreshInProgress = tagRefreshQueue.Count > 0;
        lastTagRefreshProcess = DateTime.MinValue;
    }

    public string? GetDesignBase64(Guid designId)
    {
        return designBase64Subscriber.InvokeFunc(designId);
    }

    /// <summary>
    /// Get a design as a parsed JObject from Glamourer.
    /// </summary>
    public JObject? GetDesignJObject(Guid designId)
    {
        try
        {
            return designJObjectSubscriber.InvokeFunc(designId);
        }
        catch (Exception ex)
        {
            log.Error($"[ModSnapshot] GetDesignJObject failed for {designId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get the Glamourer tags for a design. Tags are read from the design's
    /// JObject and cached. The cache is rebuilt incrementally by ProcessTagRefresh()
    /// and refreshed on demand via RequestTagRefresh().
    /// </summary>
    public IReadOnlyList<string> GetDesignTags(Guid designId)
    {
        if (designTagsCache.TryGetValue(designId, out var cached))
            return cached;

        // While a background revalidation is warming or rebuilding the cache,
        // avoid a synchronous IPC call for every not-yet-refreshed design in a
        // single frame. Those entries get populated by ProcessTagRefresh() shortly.
        if (tagRefreshInProgress)
            return Array.Empty<string>();

        var tags = ReadTagsFromDesign(designId);
        designTagsCache[designId] = tags;
        return tags;
    }

    /// <summary>
    /// Called every frame while a tag-based collection is visible. Drains the
    /// refresh queue in small batches. It does not poll periodically; new
    /// refresh cycles are requested explicitly via <see cref="RequestTagRefresh"/>.
    /// </summary>
    public void ProcessTagRefresh()
    {
        if (!tagRefreshInProgress)
        {
            // Cold start or partial cache: queue any designs we have not read yet.
            if (cachedDesignList != null)
            {
                foreach (var key in cachedDesignList.Keys)
                {
                    if (!designTagsCache.ContainsKey(key))
                        tagRefreshQueue.Enqueue(key);
                }
                tagRefreshInProgress = tagRefreshQueue.Count > 0;
            }
        }

        if (!tagRefreshInProgress)
            return;

        var now = DateTime.UtcNow;

        // At most one batch per frame, no matter how many UI callers invoke this.
        if (now - lastTagRefreshProcess < TagRefreshProcessThrottle)
            return;

        lastTagRefreshProcess = now;
        int processed = 0;
        while (processed < MaxTagRefreshesPerProcess && tagRefreshQueue.TryDequeue(out var designId))
        {
            if (cachedDesignList != null && cachedDesignList.ContainsKey(designId))
                designTagsCache[designId] = ReadTagsFromDesign(designId);
            processed++;
        }

        if (tagRefreshQueue.Count == 0)
            tagRefreshInProgress = false;
    }

    private List<string> ReadTagsFromDesign(Guid designId)
    {
        var design = GetDesignJObject(designId);
        if (design?["Tags"] is not JArray tagsArray)
            return new List<string>();

        return tagsArray
            .Where(t => t.Type == JTokenType.String)
            .Select(t => t.Value<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .ToList();
    }

    /// <summary>
    /// Get the last updated date for a design. Falls back to the creation date
    /// if no last-updated timestamp is available. Dates are fetched once per
    /// design and cached until a design is applied, deleted, or the design list
    /// changes (new/removed designs).
    /// </summary>
    public DateTime GetDesignLastEdit(Guid designId)
    {
        if (!designDateCache.TryGetValue(designId, out var date))
        {
            date = ReadLastEditFromDesign(designId);
            designDateCache[designId] = date;
        }

        return date;
    }

    private DateTime ReadLastEditFromDesign(Guid designId)
    {
        var design = GetDesignJObject(designId);
        if (design == null)
            return DateTime.MinValue;

        var lastEdit = ParseDesignDate(design["LastEdit"]);
        if (lastEdit != DateTime.MinValue)
            return lastEdit;

        return ParseDesignDate(design["CreationDate"]);
    }

    private static DateTime ParseDesignDate(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null)
            return DateTime.MinValue;

        if (token.Type == JTokenType.Date)
            return token.ToObject<DateTime>();

        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
        {
            var value = token.ToObject<long>();
            return value > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
        }

        if (token.Type == JTokenType.String)
        {
            var text = token.ToString();
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                return parsed;

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            {
                return unix > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime
                    : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }
        }

        return DateTime.MinValue;
    }

    /// <summary>
    /// Extract equipment ItemIds from a Glamourer design JObject.
    /// Returns slot name → ItemId for non-empty slots.
    /// </summary>
    public Dictionary<string, uint> GetDesignEquipment(Guid designId)
    {
        var result = new Dictionary<string, uint>();
        var design = GetDesignJObject(designId);
        if (design == null)
            return result;

        var equipment = design["Equipment"] as JObject;
        if (equipment == null)
            return result;

        foreach (var prop in equipment.Properties())
        {
            var itemId = prop.Value["ItemId"]?.ToObject<uint>() ?? 0;
            if (itemId > 0)
                result[prop.Name] = itemId;
        }

        return result;
    }

    public List<string> GetUniqueFolderPaths()
    {
        var designs = GetDesignList();
        var paths = designs.Values
            .Select(d => d.FullPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path)
            .ToList();

        return paths;
    }

    /// <summary>
    /// Apply a design to the player character using Glamourer IPC.
    /// </summary>
    /// <param name="designId">The GUID of the design to apply</param>
    /// <param name="equipmentOnly">If true, apply only equipment (not customization). Default is false (apply full design)</param>
    /// <returns>Status code: 0=Success, 1=DesignNotFound, 2=ActorNotFound, 3=InvalidKey</returns>
    public int ApplyDesign(Guid designId, bool equipmentOnly = false)
    {
        try
        {
            // Flags: 0x01=Once, 0x02=Equipment, 0x04=Customization
            // Full design: Once | Equipment | Customization = 0x07
            // Equipment only: Once | Equipment = 0x03
            ulong designFlags = equipmentOnly ? 0x03uL : 0x07uL;
            
            // Apply to player (object index 0), key=0 (no locking)
            int result = applyDesignSubscriber.InvokeFunc(designId, 0, 0, designFlags);
            InvalidateDesignListCache(clearTags: false);
            
            if (result == 0)
            {
                var applyType = equipmentOnly ? "(equipment only)" : "(full design)";
                log.Information($"Successfully applied design: {designId} {applyType}");

                configuration.LastAppliedAt[designId] = DateTime.UtcNow;
                configuration.Save();
            }
            else
            {
                log.Warning($"Failed to apply design {designId}. Status code: {result}");
            }
            
            return result;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Error applying design {designId}");
            return -1; // Error status
        }
    }

    /// <summary>
    /// Delete a design from Glamourer using IPC.
    /// </summary>
    /// <param name="designId">The GUID of the design to delete</param>
    /// <returns>Status code: 0=Success, non-zero=Failure</returns>
    public int DeleteDesign(Guid designId)
    {
        try
        {
            int result = deleteDesignSubscriber.InvokeFunc(designId);
            InvalidateDesignListCache();
            
            if (result == 0)
            {
                log.Information($"Successfully deleted design: {designId}");
            }
            else
            {
                log.Warning($"Failed to delete design {designId}. Status code: {result}");
            }
            
            return result;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Error deleting design {designId}");
            return -1; // Error status
        }
    }
}