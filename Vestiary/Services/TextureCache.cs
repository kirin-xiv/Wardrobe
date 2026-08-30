using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace Vestiary.Services;

/// <summary>
/// Caches textures loaded from file paths to avoid reloading every frame.
/// Uses LRU eviction: when the cache exceeds <see cref="MaxCacheSize"/>, the least
/// recently accessed texture is evicted. The underlying GPU resources are owned by
/// Dalamud's shared texture manager, so dropping the reference (removing it from the
/// dictionary) is what releases them — no explicit Dispose is available or required.
/// </summary>
public class TextureCache : IDisposable
{
    private const int MaxCacheSize = 100;

    private readonly ITextureProvider textureProvider;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> cache = new();

    private sealed class Entry
    {
        public ISharedImmediateTexture? Texture;
        public long LastAccessTick;
    }

    public TextureCache(ITextureProvider textureProvider)
    {
        this.textureProvider = textureProvider;
    }

    /// <summary>
    /// Get a cached texture or load it from file if not cached.
    /// Each call bumps the access timestamp so actively-displayed textures stay resident.
    /// </summary>
    public ISharedImmediateTexture? GetOrLoadTexture(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        lock (gate)
        {
            if (cache.TryGetValue(filePath, out var entry))
            {
                entry.LastAccessTick = DateTime.UtcNow.Ticks;
                cache[filePath] = entry;
                return entry.Texture;
            }
        }

        ISharedImmediateTexture? texture;
        try
        {
            texture = textureProvider.GetFromFile(new FileInfo(filePath));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Failed to load texture from {filePath}");
            return null;
        }

        if (texture == null)
            return null;

        lock (gate)
        {
            EvictIfNeededLocked();
            cache[filePath] = new Entry
            {
                Texture = texture,
                LastAccessTick = DateTime.UtcNow.Ticks
            };
        }

        return texture;
    }

    /// <summary>
    /// Remove a texture from cache (e.g., when its file is deleted).
    /// </summary>
    public void InvalidateTexture(string filePath)
    {
        lock (gate)
        {
            cache.Remove(filePath);
        }
    }

    /// <summary>
    /// Clear all cached texture references.
    /// </summary>
    public void ClearAll()
    {
        lock (gate)
        {
            cache.Clear();
        }
    }

    public void Dispose()
    {
        ClearAll();
    }

    private void EvictIfNeededLocked()
    {
        if (cache.Count < MaxCacheSize)
            return;

        // Remove the entry with the oldest access timestamp.
        var oldest = cache.MinBy(kv => kv.Value.LastAccessTick);
        cache.Remove(oldest.Key);
    }
}
