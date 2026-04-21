namespace AgXR.App.Services;

using AgXR.App.Data;
using AgXR.App.Models;
using Android.Content;
using GenerativeAI;
using GenerativeAI.Types;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Service for capturing and saving geo-tagged observations.
/// Coordinates camera, GPS, voice input, and Gemini for extraction.
/// </summary>
public class GeoTagService
{
    private readonly GeoTagDatabase _database;
    private readonly GenerativeModel _geminiModel;
    private readonly Context _context;
    
    public GeoTagService(Context context, string apiKey)
    {
        _context = context;
        _database = new GeoTagDatabase();
        
        var googleAi = new GoogleAi(apiKey);
        _geminiModel = googleAi.CreateGenerativeModel("gemini-2.0-flash");
    }
    
    /// <summary>
    /// Process a geo-tag capture: send to Gemini for extraction, then save to database.
    /// </summary>
    /// <param name="imagePath">Path to the captured image</param>
    /// <param name="latitude">GPS latitude</param>
    /// <param name="longitude">GPS longitude</param>
    /// <param name="accuracy">GPS accuracy in meters</param>
    /// <param name="voiceTranscript">Transcribed voice description from farmer</param>
    public async Task<GeoTag?> ProcessGeoTagAsync(
        string imagePath,
        double latitude,
        double longitude,
        double accuracy,
        string voiceTranscript)
    {
        try
        {
            var hasImage = !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath);
            Android.Util.Log.Info("AgXR", $"Processing geo-tag: '{voiceTranscript}' (image={hasImage})");

            // Build prompt for Gemini to extract structured data. When an image is attached, ask it to
            // combine what it sees with the farmer's description; otherwise fall back to description only.
            var imageClause = hasImage
                ? "Use the attached photo together with the farmer's description to produce the most accurate summary. If the description is vague or empty, describe what is visible in the photo."
                : "Base your response on the farmer's description.";

            var prompt = $@"You are a farm observation assistant. A farmer has taken a photo and described what they see.

Farmer's description: ""{voiceTranscript}""

{imageClause}

Respond in JSON format:
{{
  ""category"": ""<one of: assets, compliance, stock_pasture, weed_pest>"",
  ""description"": ""<clear summary of the observation>"",
  ""action"": ""<what needs to be done, or null if no action mentioned>""
}}

Category guidelines:
- assets: Farm infrastructure like fences, gates, troughs, buildings
- compliance: Environmental or regulatory observations
- stock_pasture: Livestock health or pasture conditions
- weed_pest: Weeds, pests, or plant diseases

Respond ONLY with the JSON object, no other text.";

            var request = new GenerateContentRequest();
            request.AddText(prompt);
            if (hasImage)
            {
                request.AddInlineFile(imagePath!);
            }

            var response = await _geminiModel.GenerateContentAsync(request);
            var responseText = response.Text() ?? "";
            
            Android.Util.Log.Info("AgXR", $"Gemini response: {responseText}");
            
            // Parse JSON response
            var extracted = ParseGeminiResponse(responseText, voiceTranscript);
            
            var geoTag = new GeoTag
            {
                Category = extracted.Category,
                Description = extracted.Description,
                Action = extracted.Action,
                Latitude = latitude,
                Longitude = longitude,
                Accuracy = accuracy,
                ImagePath = imagePath
            };
            
            // Save to database
            await _database.SaveGeoTagAsync(geoTag);
            Android.Util.Log.Info("AgXR", $"Saved geo-tag: {geoTag.Category} - {geoTag.Description}");
            
            return geoTag;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"GeoTag processing failed: {ex.Message}. Saving with raw transcript as fallback.");

            // Fallback: save what we have so a tag still lands on the map/list even when Gemini is unreachable or rate-limited.
            try
            {
                var fallbackTag = new GeoTag
                {
                    Category = "assets",
                    Description = string.IsNullOrWhiteSpace(voiceTranscript) ? "(no description)" : voiceTranscript,
                    Action = null,
                    Latitude = latitude,
                    Longitude = longitude,
                    Accuracy = accuracy,
                    ImagePath = imagePath
                };
                await _database.SaveGeoTagAsync(fallbackTag);
                Android.Util.Log.Info("AgXR", $"Saved fallback geo-tag at {latitude}, {longitude}");
                return fallbackTag;
            }
            catch (Exception saveEx)
            {
                Android.Util.Log.Error("AgXR", $"Fallback save also failed: {saveEx.Message}");
                return null;
            }
        }
    }
    
    /// <summary>
    /// Parse Gemini's JSON response into structured data.
    /// </summary>
    private (string Category, string Description, string? Action) ParseGeminiResponse(
        string response, string fallbackDescription)
    {
        try
        {
            // Clean up response - remove markdown code blocks if present
            var json = response.Trim();
            if (json.StartsWith("```json"))
                json = json.Substring(7);
            if (json.StartsWith("```"))
                json = json.Substring(3);
            if (json.EndsWith("```"))
                json = json.Substring(0, json.Length - 3);
            json = json.Trim();
            
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            var category = root.TryGetProperty("category", out var catProp) 
                ? catProp.GetString() ?? "assets" 
                : "assets";
            
            var description = root.TryGetProperty("description", out var descProp)
                ? descProp.GetString() ?? fallbackDescription
                : fallbackDescription;
                
            var action = root.TryGetProperty("action", out var actProp)
                ? actProp.GetString()
                : null;
            
            // Validate category
            if (!GeoTagCategories.All.Contains(category))
                category = "assets";
                
            return (category, description, action);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("AgXR", $"Failed to parse Gemini response: {ex.Message}");
            return ("assets", fallbackDescription, null);
        }
    }
    
    /// <summary>
    /// Save an image to the app's local storage.
    /// </summary>
    public string SaveImage(byte[] imageData)
    {
        var filename = $"geotag_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg";
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeoTagImages");
        
        Directory.CreateDirectory(directory);
        var filepath = Path.Combine(directory, filename);
        
        File.WriteAllBytes(filepath, imageData);
        Android.Util.Log.Info("AgXR", $"Saved image: {filepath}");
        
        return filepath;
    }
    
    /// <summary>
    /// Get all saved geo-tags.
    /// </summary>
    public Task<List<GeoTag>> GetAllTagsAsync() => _database.GetGeoTagsAsync();
    
    /// <summary>
    /// Get tags by category.
    /// </summary>
    public Task<List<GeoTag>> GetTagsByCategoryAsync(string category) 
        => _database.GetGeoTagsAsync(category);
    
    /// <summary>
    /// Get count of unsynced tags.
    /// </summary>
    public async Task<int> GetUnsyncedCountAsync()
    {
        var unsynced = await _database.GetUnsyncedTagsAsync();
        return unsynced.Count;
    }
}
