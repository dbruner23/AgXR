namespace AgXR.App.Models;

using SQLite;
using System;

/// <summary>
/// Represents a geo-tagged observation captured by the farmer.
/// </summary>
[Table("GeoTags")]
public class GeoTag
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    
    /// <summary>
    /// Domain context - always "Farming" for this app.
    /// </summary>
    public string Domain { get; set; } = "Farming";
    
    /// <summary>
    /// Category of the observation.
    /// Valid values: assets, compliance, stock_pasture, weed_pest
    /// </summary>
    [Indexed]
    public string Category { get; set; } = string.Empty;
    
    /// <summary>
    /// Description extracted by Gemini from the farmer's voice.
    /// </summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// Optional action item extracted by Gemini.
    /// </summary>
    public string? Action { get; set; }
    
    /// <summary>
    /// GPS Latitude in decimal degrees.
    /// </summary>
    public double Latitude { get; set; }
    
    /// <summary>
    /// GPS Longitude in decimal degrees.
    /// </summary>
    public double Longitude { get; set; }
    
    /// <summary>
    /// GPS accuracy in meters.
    /// </summary>
    public double Accuracy { get; set; }
    
    /// <summary>
    /// Local file path to the captured image.
    /// </summary>
    public string ImagePath { get; set; } = string.Empty;
    
    /// <summary>
    /// When the geo-tag was captured.
    /// </summary>
    [Indexed]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Flag for sync status with cloud backend.
    /// </summary>
    public bool IsSynced { get; set; } = false;

    /// <summary>
    /// Whether this tag should be tracked in AR view.
    /// </summary>
    public bool IsTracked { get; set; } = true;
}

/// <summary>
/// Valid categories for geo-tags.
/// </summary>
public static class GeoTagCategories
{
    public const string Assets = "assets";
    public const string Compliance = "compliance";
    public const string StockPasture = "stock_pasture";
    public const string WeedPest = "weed_pest";
    
    public static readonly string[] All = { Assets, Compliance, StockPasture, WeedPest };
}
