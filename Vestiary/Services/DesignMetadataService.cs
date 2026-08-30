using System;
using System.Collections.Generic;
using System.Linq;
using Vestiary.Models;

namespace Vestiary.Services;

public class DesignMetadataService
{
    private readonly Configuration configuration;
    private readonly GlamourerService glamourerService;

    // Metadata is looked up for every card, every frame. A linear scan of
    // configuration.DesignMetadata per card becomes O(cards x metadata); index it
    // once and rebuild lazily when the list is replaced (e.g. Wardrobe migration)
    // or mutated through this service.
    private List<DesignMetadata>? _indexedList;
    private Dictionary<Guid, DesignMetadata>? _index;

    public DesignMetadataService(Configuration configuration, GlamourerService glamourerService)
    {
        this.configuration = configuration;
        this.glamourerService = glamourerService;
    }

    private Dictionary<Guid, DesignMetadata> GetIndex()
    {
        var list = configuration.DesignMetadata ?? new();
        if (_index == null || !ReferenceEquals(_indexedList, list))
        {
            _index = new Dictionary<Guid, DesignMetadata>();
            foreach (var m in list)
                _index[m.DesignId] = m;
            _indexedList = list;
        }
        return _index;
    }

    /// <summary>
    /// Get metadata for a specific design, or null if not found.
    /// </summary>
    public DesignMetadata? GetMetadata(Guid designId)
    {
        return GetIndex().TryGetValue(designId, out var m) ? m : null;
    }

    /// <summary>
    /// Create or update metadata for a design.
    /// </summary>
    public void UpsertMetadata(Guid designId, string nickname = "", string customImagePath = "")
    {
        var existing = GetMetadata(designId);
        if (existing != null)
        {
            existing.Nickname = nickname;
            existing.CustomImagePath = customImagePath;
        }
        else
        {
            var metadata = new DesignMetadata(designId, nickname, customImagePath);
            configuration.DesignMetadata.Add(metadata);
            _index = null; // stale after Add; rebuild lazily
        }
        configuration.Save();
    }

    /// <summary>
    /// Delete metadata for a design.
    /// </summary>
    public void DeleteMetadata(Guid designId)
    {
        var metadata = GetMetadata(designId);
        if (metadata != null)
        {
            configuration.DesignMetadata.Remove(metadata);
            _index = null; // stale after Remove; rebuild lazily
            configuration.Save();
        }
    }

    /// <summary>
    /// Set nickname without touching the custom image path.
    /// </summary>
    public void SetNickname(Guid designId, string nickname)
    {
        var existing = GetMetadata(designId);
        UpsertMetadata(designId, nickname: nickname, customImagePath: existing?.CustomImagePath ?? "");
    }

    /// <summary>
    /// Set custom image path without touching the nickname.
    /// </summary>
    public void SetCustomImage(Guid designId, string path)
    {
        var existing = GetMetadata(designId);
        UpsertMetadata(designId, nickname: existing?.Nickname ?? "", customImagePath: path);
    }

    /// <summary>
    /// Get display name for a design: returns Nickname if set, otherwise Glamourer's DisplayName.
    /// </summary>
    public string GetDisplayName(Guid designId)
    {
        var metadata = GetMetadata(designId);
        if (!string.IsNullOrEmpty(metadata?.Nickname))
        {
            return metadata.Nickname;
        }

        // Fallback to Glamourer's DisplayName
        var designs = glamourerService.GetDesignList();
        if (designs.TryGetValue(designId, out var design))
        {
            return design.DisplayName;
        }

        return "Unknown Design";
    }
}
