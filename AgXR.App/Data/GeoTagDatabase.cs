namespace AgXR.App.Data;

using AgXR.App.Models;
using SQLite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

/// <summary>
/// SQLite database for storing geo-tagged observations.
/// Implements store-locally-then-sync pattern.
/// </summary>
public class GeoTagDatabase
{
    private readonly SQLiteAsyncConnection _database;
    
    public GeoTagDatabase()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "geotags.db3");
        
        _database = new SQLiteAsyncConnection(dbPath);
        _database.CreateTableAsync<GeoTag>().Wait();
    }
    
    /// <summary>
    /// Save a new geo-tag to the local database.
    /// </summary>
    public Task<int> SaveGeoTagAsync(GeoTag tag)
    {
        tag.Timestamp = DateTime.UtcNow;
        tag.IsSynced = false;
        return _database.InsertAsync(tag);
    }
    
    /// <summary>
    /// Get all geo-tags, optionally filtered by category.
    /// </summary>
    public Task<List<GeoTag>> GetGeoTagsAsync(string? category = null)
    {
        if (string.IsNullOrEmpty(category))
        {
            return _database.Table<GeoTag>()
                .OrderByDescending(t => t.Timestamp)
                .ToListAsync();
        }
        
        return _database.Table<GeoTag>()
            .Where(t => t.Category == category)
            .OrderByDescending(t => t.Timestamp)
            .ToListAsync();
    }
    
    /// <summary>
    /// Get geo-tags that haven't been synced yet.
    /// </summary>
    public Task<List<GeoTag>> GetUnsyncedTagsAsync()
    {
        return _database.Table<GeoTag>()
            .Where(t => !t.IsSynced)
            .ToListAsync();
    }
    
    /// <summary>
    /// Mark a geo-tag as synced with the cloud.
    /// </summary>
    public Task<int> MarkAsSyncedAsync(int tagId)
    {
        return _database.ExecuteAsync(
            "UPDATE GeoTags SET IsSynced = 1 WHERE Id = ?", tagId);
    }
    
    /// <summary>
    /// Get a specific geo-tag by ID.
    /// </summary>
    public Task<GeoTag> GetGeoTagAsync(int id)
    {
        return _database.Table<GeoTag>()
            .Where(t => t.Id == id)
            .FirstOrDefaultAsync();
    }
    
    /// <summary>
    /// Delete a geo-tag.
    /// </summary>
    public Task<int> DeleteGeoTagAsync(GeoTag tag)
    {
        return _database.DeleteAsync(tag);
    }
    
    /// <summary>
    /// Get count of all tags.
    /// </summary>
    public Task<int> GetCountAsync()
    {
        return _database.Table<GeoTag>().CountAsync();
    }
}
