using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace ARDiscard.GameData;

/// <summary>
/// Market data response from Universalis API.
/// </summary>
public class MarketDataResponse
{
    [JsonPropertyName("itemID")]
    public long ItemId { get; set; }

    [JsonPropertyName("lastUploadTime")]
    public long LastUploadTime { get; set; }

    [JsonPropertyName("listings")]
    public List<MarketDataListing> Listings { get; set; } = new();

    [JsonPropertyName("averagePrice")]
    public double AveragePrice { get; set; }

    [JsonPropertyName("averagePriceNQ")]
    public double AveragePriceNq { get; set; }

    [JsonPropertyName("averagePriceHQ")]
    public double AveragePriceHq { get; set; }

    /// <summary>
    /// Timestamp when this data was fetched (for caching).
    /// </summary>
    public long FetchTimestamp { get; set; }
    
    /// <summary>
    /// Get the cheapest listing across all servers.
    /// </summary>
    public MarketDataListing? GetCheapestListing()
    {
        return Listings?.OrderBy(l => l.PricePerUnit).FirstOrDefault();
    }
    
    /// <summary>
    /// Get listings grouped by world for display purposes.
    /// </summary>
    public Dictionary<string, List<MarketDataListing>> GetListingsByWorld()
    {
        if (Listings == null) return new Dictionary<string, List<MarketDataListing>>();
        
        return Listings
            .GroupBy(l => l.WorldName)
            .ToDictionary(
                g => g.Key, 
                g => g.OrderBy(l => l.PricePerUnit).ToList() // All listings sorted by price
            );
    }
}

/// <summary>
/// Individual market listing from Universalis API.
/// </summary>
public class MarketDataListing
{
    [JsonPropertyName("pricePerUnit")]
    public long PricePerUnit { get; set; }

    [JsonPropertyName("quantity")]
    public long Quantity { get; set; }

    [JsonPropertyName("hq")]
    public bool Hq { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("worldName")]
    public string WorldName { get; set; } = string.Empty;
}

/// <summary>
/// Simple client for fetching market data from Universalis API.
/// </summary>
internal class UniversalisClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Dictionary<uint, MarketDataResponse> _cache = new();
    private readonly Dictionary<uint, DateTime> _failedItems = new(); // Track items that failed to fetch
    private readonly Configuration _configuration;
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly TimeSpan _minRequestInterval = TimeSpan.FromMilliseconds(500); // Conservative rate limiting per Universalis docs
    
    public UniversalisClient(Configuration configuration)
    {
        _configuration = configuration;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://universalis.app/api/v2/"),
            Timeout = TimeSpan.FromSeconds(30) // Universalis recommends longer timeouts
        };
        
        // Proper User-Agent as recommended by Universalis docs
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ARDiscard/1.0.0 (https://github.com/user/ARDiscard)");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        
        // Add X-FFXIV-Using-Unofficial-Parse-API header if using game data parsing
        _httpClient.DefaultRequestHeaders.Add("X-FFXIV-Using-Unofficial-Parse-API", "true");
    }

    /// <summary>
    /// Get market data for an item. Returns cached data if available and not expired.
    /// </summary>
    public async Task<MarketDataResponse?> GetMarketDataAsync(uint itemId, string worldName, CancellationToken cancellationToken = default, bool forceUseDataCenter = false)
    {
        // Check cache first
        if (_cache.TryGetValue(itemId, out var cached))
        {
            var cacheTimeout = TimeSpan.FromMinutes(_configuration.MarketPrice.CacheTimeoutMinutes);
            var age = DateTimeOffset.Now.ToUnixTimeMilliseconds() - cached.FetchTimestamp;
            if (age < cacheTimeout.TotalMilliseconds)
            {
                return cached;
            }
            _cache.Remove(itemId);
        }

        // Check if this item failed recently
        var failedItemTimeout = TimeSpan.FromMinutes(1); // Retry failed items after 1 minute
        if (_failedItems.TryGetValue(itemId, out var failTime))
        {
            if (DateTime.Now - failTime < failedItemTimeout)
            {
                return null; // Don't retry failed items too quickly
            }
            _failedItems.Remove(itemId);
        }

        // Rate limiting - don't make requests too frequently
        var timeSinceLastRequest = DateTime.Now - _lastRequestTime;
        if (timeSinceLastRequest < _minRequestInterval)
        {
            var delayTime = _minRequestInterval - timeSinceLastRequest;
            await Task.Delay(delayTime, cancellationToken).ConfigureAwait(false);
        }
        _lastRequestTime = DateTime.Now;

        try
        {
            // Determine query target based on configuration
            string queryTarget = worldName;
            if (_configuration.MarketPrice.UseDataCenter || forceUseDataCenter)
            {
                var dataCenter = GetDataCenterForWorld(worldName);
                if (dataCenter != null)
                {
                    queryTarget = dataCenter;
                }
            }
            
            // Construct URL following Universalis API standards
            // Format: /api/v2/{world}/{itemId}?listings={count}&entries={count}
            var url = $"{queryTarget}/{itemId}?listings=5&entries=0&statsWithin=7200000&entriesWithin=7200000";
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            
            // Handle specific HTTP status codes as per Universalis documentation
            if (!response.IsSuccessStatusCode)
            {
                // 404 means item doesn't exist on this world/DC
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Try fallback if we're querying a specific world and fallback is enabled
                    if (!_configuration.MarketPrice.UseDataCenter && !forceUseDataCenter && _configuration.MarketPrice.FallbackToDataCenter && queryTarget == worldName)
                    {
                        var dataCenter = GetDataCenterForWorld(worldName);
                        if (dataCenter != null && dataCenter != worldName)
                        {
                            // Recursively try with data center
                            return await GetMarketDataAsync(itemId, worldName, cancellationToken, true).ConfigureAwait(false);
                        }
                    }
                    
                    _failedItems[itemId] = DateTime.Now;
                    return null;
                }
                
                // 429 means rate limited - wait longer before retrying
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _failedItems[itemId] = DateTime.Now.AddMinutes(5); // Wait longer for rate limits
                    return null;
                }
                
                // Other errors
                _failedItems[itemId] = DateTime.Now;
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var data = await JsonSerializer.DeserializeAsync<MarketDataResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            
            if (data != null)
            {
                data.FetchTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                _cache[itemId] = data;
                
                // Remove from failed items if it was there
                _failedItems.Remove(itemId);
            }
            else
            {
                // Mark as failed if we got null data
                _failedItems[itemId] = DateTime.Now;
            }
            
            return data;
        }
        catch (TaskCanceledException)
        {
            // Handle timeouts specifically - try fallback if applicable
            if (!_configuration.MarketPrice.UseDataCenter && !forceUseDataCenter && _configuration.MarketPrice.FallbackToDataCenter)
            {
                var dataCenter = GetDataCenterForWorld(worldName);
                if (dataCenter != null && dataCenter != worldName)
                {
                    return await GetMarketDataAsync(itemId, worldName, cancellationToken, true).ConfigureAwait(false);
                }
            }
            _failedItems[itemId] = DateTime.Now;
            return null;
        }
        catch (HttpRequestException)
        {
            // Handle network errors - try fallback if applicable
            if (!_configuration.MarketPrice.UseDataCenter && !forceUseDataCenter && _configuration.MarketPrice.FallbackToDataCenter)
            {
                var dataCenter = GetDataCenterForWorld(worldName);
                if (dataCenter != null && dataCenter != worldName)
                {
                    return await GetMarketDataAsync(itemId, worldName, cancellationToken, true).ConfigureAwait(false);
                }
            }
            _failedItems[itemId] = DateTime.Now;
            return null;
        }
        catch (Exception)
        {
            // Handle other errors
            _failedItems[itemId] = DateTime.Now;
            return null;
        }
    }

    /// <summary>
    /// Get the lowest market price for an item (NQ or HQ).
    /// </summary>
    public async Task<(long price, bool isHq)?> GetLowestPriceAsync(uint itemId, string worldName, CancellationToken cancellationToken = default)
    {
        var data = await GetMarketDataAsync(itemId, worldName, cancellationToken).ConfigureAwait(false);
        if (data?.Listings == null || data.Listings.Count == 0)
        {
            return null;
        }

        var lowestListing = data.Listings
            .OrderBy(l => l.PricePerUnit)
            .FirstOrDefault();

        return lowestListing != null ? (lowestListing.PricePerUnit, lowestListing.Hq) : null;
    }

    /// <summary>
    /// Get all worlds in a data center
    /// </summary>
    public static List<string> GetWorldsInDataCenter(string dataCenter)
    {
        var dcToWorlds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Aether
            { "Aether", new List<string> { "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren" } },
            
            // Primal
            { "Primal", new List<string> { "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros" } },
            
            // Crystal
            { "Crystal", new List<string> { "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera" } },
            
            // Chaos (EU)
            { "Chaos", new List<string> { "Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan" } },
            
            // Light (EU)
            { "Light", new List<string> { "Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark" } },
            
            // Elemental (JP)
            { "Elemental", new List<string> { "Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Ramuh", "Tonberry", "Typhon", "Unicorn" } },
            
            // Gaia (JP)
            { "Gaia", new List<string> { "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima" } },
            
            // Mana (JP)
            { "Mana", new List<string> { "Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan" } },
            
            // Meteor (JP)
            { "Meteor", new List<string> { "Belias", "Mandragora", "Shinryu", "Valefor", "Yojimbo", "Zeromus" } },
            
            // Materia (OCE)
            { "Materia", new List<string> { "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan" } }
        };

        return dcToWorlds.GetValueOrDefault(dataCenter, new List<string>());
    }

    /// <summary>
    /// Get data center name for a world - fallback for when world queries fail
    /// </summary>
    private string GetDataCenterForWorld(string worldName)
    {
        // Common world to data center mappings
        var worldToDc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Aether
            { "Adamantoise", "Aether" }, { "Cactuar", "Aether" }, { "Faerie", "Aether" },
            { "Gilgamesh", "Aether" }, { "Jenova", "Aether" }, { "Midgardsormr", "Aether" },
            { "Sargatanas", "Aether" }, { "Siren", "Aether" },
            
            // Primal
            { "Behemoth", "Primal" }, { "Excalibur", "Primal" }, { "Exodus", "Primal" },
            { "Famfrit", "Primal" }, { "Hyperion", "Primal" }, { "Lamia", "Primal" },
            { "Leviathan", "Primal" }, { "Ultros", "Primal" },
            
            // Crystal
            { "Balmung", "Crystal" }, { "Brynhildr", "Crystal" }, { "Coeurl", "Crystal" },
            { "Diabolos", "Crystal" }, { "Goblin", "Crystal" }, { "Malboro", "Crystal" },
            { "Mateus", "Crystal" }, { "Zalera", "Crystal" },
            
            // Chaos (EU)
            { "Cerberus", "Chaos" }, { "Louisoix", "Chaos" }, { "Moogle", "Chaos" },
            { "Omega", "Chaos" }, { "Phantom", "Chaos" }, { "Ragnarok", "Chaos" },
            { "Sagittarius", "Chaos" }, { "Spriggan", "Chaos" },
            
            // Light (EU)
            { "Alpha", "Light" }, { "Lich", "Light" }, { "Odin", "Light" },
            { "Phoenix", "Light" }, { "Raiden", "Light" }, { "Shiva", "Light" },
            { "Twintania", "Light" }, { "Zodiark", "Light" },
            
            // Elemental (JP)
            { "Aegis", "Elemental" }, { "Atomos", "Elemental" }, { "Carbuncle", "Elemental" },
            { "Garuda", "Elemental" }, { "Gungnir", "Elemental" }, { "Kujata", "Elemental" },
            { "Ramuh", "Elemental" }, { "Tonberry", "Elemental" }, { "Typhon", "Elemental" },
            { "Unicorn", "Elemental" },
            
            // Gaia (JP)
            { "Alexander", "Gaia" }, { "Bahamut", "Gaia" }, { "Durandal", "Gaia" },
            { "Fenrir", "Gaia" }, { "Ifrit", "Gaia" }, { "Ridill", "Gaia" },
            { "Tiamat", "Gaia" }, { "Ultima", "Gaia" },
            
            // Mana (JP)
            { "Anima", "Mana" }, { "Asura", "Mana" }, { "Chocobo", "Mana" },
            { "Hades", "Mana" }, { "Ixion", "Mana" }, { "Masamune", "Mana" },
            { "Pandaemonium", "Mana" }, { "Titan", "Mana" },
            
            // Meteor (JP)
            { "Belias", "Meteor" }, { "Mandragora", "Meteor" }, { "Shinryu", "Meteor" },
            { "Valefor", "Meteor" }, { "Yojimbo", "Meteor" }, { "Zeromus", "Meteor" },
            
            // Materia (OCE)
            { "Bismarck", "Materia" }, { "Ravana", "Materia" }, { "Sephirot", "Materia" },
            { "Sophia", "Materia" }, { "Zurvan", "Materia" }
        };

        return worldToDc.GetValueOrDefault(worldName, worldName); // Fallback to world name if DC not found
    }

    /// <summary>
    /// Check if an item is likely to be marketable (basic validation)
    /// </summary>
    public static bool IsLikelyMarketable(uint itemId)
    {
        // Items with IDs less than 20 are usually currencies/special items
        if (itemId < 20) return false;
        
        // Very high item IDs might be system items
        if (itemId > 50000) return false;
        
        return true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient?.Dispose();
        }
    }
} 