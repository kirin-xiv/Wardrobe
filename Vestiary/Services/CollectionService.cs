using System;
using System.Collections.Generic;
using System.Linq;
using Vestiary.Models;

namespace Vestiary.Services;

public class CollectionService
{
    private readonly Configuration configuration;
    private readonly GlamourerService glamourerService;

    public CollectionService(Configuration configuration, GlamourerService glamourerService)
    {
        this.configuration = configuration;
        this.glamourerService = glamourerService;
    }

    /// <summary>
    /// Get all collections.
    /// </summary>
    public List<Collection> GetCollections()
    {
        return configuration.Collections;
    }

    /// <summary>
    /// Create a new collection.
    /// </summary>
    public Collection CreateCollection(string name, List<string> folderPaths, List<string>? tags = null)
    {
        var collection = new Collection(name, folderPaths, configuration.Collections.Count, tags);
        configuration.Collections.Add(collection);
        configuration.Save();
        return collection;
    }

    /// <summary>
    /// Update an existing collection.
    /// </summary>
    public bool UpdateCollection(Guid id, string name, List<string> folderPaths, List<string>? tags = null)
    {
        var collection = configuration.Collections.FirstOrDefault(c => c.Id == id);
        if (collection == null)
            return false;

        collection.Name = name;
        collection.FolderPaths = folderPaths ?? new();
        collection.Tags = tags ?? new();
        configuration.Save();
        return true;
    }

    /// <summary>
    /// Delete a collection by ID.
    /// </summary>
    public bool DeleteCollection(Guid id)
    {
        var collection = configuration.Collections.FirstOrDefault(c => c.Id == id);
        if (collection == null)
            return false;

        configuration.Collections.Remove(collection);
        configuration.Save();
        return true;
    }

    /// <summary>
    /// Swap the order of two collections by their indices in the sorted list.
    /// </summary>
    public void SwapOrder(int indexA, int indexB)
    {
        var sorted = configuration.Collections.OrderBy(c => c.Order).ToList();
        if (indexA < 0 || indexA >= sorted.Count || indexB < 0 || indexB >= sorted.Count)
            return;

        // Swap in the underlying list
        var a = sorted[indexA];
        var b = sorted[indexB];
        (a.Order, b.Order) = (b.Order, a.Order);
        configuration.Save();
    }

    /// <summary>
    /// Get all designs that match the collection's folder paths.
    /// - If the collection has paths: returns designs matching any of those paths (prefix matching)
    /// - If the collection has NO paths: returns designs not in any other collection ("Uncategorized")
    /// </summary>
    public Dictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor, bool ShownInQdb)> GetDesignsByCollection(Guid collectionId)
    {
        var collection = configuration.Collections.FirstOrDefault(c => c.Id == collectionId);
        if (collection == null)
            return new();

        return GetDesignsByCriteria(collection.FolderPaths, collection.Tags);
    }

    /// <summary>
    /// Get all designs that match the supplied folder paths and/or tags.
    /// Matching is a union: a design is included when it matches any folder path
    /// OR carries any of the requested tags. If neither criteria is supplied,
    /// returns uncategorized designs (root-level, no "/" in their path).
    /// </summary>
    public Dictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor, bool ShownInQdb)> GetDesignsByCriteria(
        List<string>? folderPaths,
        List<string>? tags)
    {
        var folders = Normalize(folderPaths);
        var tagList = Normalize(tags);

        var allDesigns = glamourerService.GetDesignList();

        bool hasFolders = folders.Count > 0;
        bool hasTags = tagList.Count > 0;

        // Drain any pending tag refresh work while a tag-based collection is visible.
        if (hasTags)
            glamourerService.ProcessTagRefresh();

        // If collection has no paths and no tags, return designs with no folder (root-level designs)
        if (!hasFolders && !hasTags)
        {
            return allDesigns
                .Where(kvp => !kvp.Value.FullPath.Contains("/"))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        var tagSet = new HashSet<string>(tagList, StringComparer.OrdinalIgnoreCase);

        return allDesigns
            .Where(kvp =>
            {
                bool folderMatch = hasFolders && folders.Any(path =>
                    kvp.Value.FullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase));

                bool tagMatch = hasTags && glamourerService.GetDesignTags(kvp.Key)
                    .Any(designTag => tagSet.Contains(designTag));

                return folderMatch || tagMatch;
            })
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Convenience wrapper for the collection editor's live preview.
    /// </summary>
    public int CountDesignsByCriteria(List<string>? folderPaths, List<string>? tags) =>
        GetDesignsByCriteria(folderPaths, tags).Count;

    private static List<string> Normalize(List<string>? values) =>
        (values ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();
}
