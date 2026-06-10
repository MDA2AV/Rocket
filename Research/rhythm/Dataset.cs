using System.Text;
using System.Text.Json;

namespace Rhythm;

/// <summary>A dataset item parsed into model fields (strings as UTF-8 bytes).</summary>
internal readonly struct Item
{
    public readonly long Id, Price, Quantity, Score, RatingCount;
    public readonly bool Active;
    public readonly byte[] Name, Category;
    public readonly byte[][] Tags;

    public Item(long id, byte[] name, byte[] category, long price, long quantity,
                bool active, byte[][] tags, long score, long ratingCount)
    {
        Id = id; Name = name; Category = category; Price = price; Quantity = quantity;
        Active = active; Tags = tags; Score = score; RatingCount = ratingCount;
    }
}

/// <summary>
/// Items parsed into model fields at startup (read-only, shared across reactor
/// threads) so the json handler serializes the full JSON from the model on every
/// request — no precomputed/cached response fragments.
/// </summary>
internal sealed class Dataset
{
    public readonly Item[] Items;
    public int Count => Items.Length;

    public static readonly Dataset Empty = new(Array.Empty<Item>());

    private Dataset(Item[] items) { Items = items; }

    public static Dataset Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = doc.RootElement;
            var items = new Item[root.GetArrayLength()];
            int i = 0;
            foreach (JsonElement e in root.EnumerateArray())
            {
                JsonElement rating = e.GetProperty("rating");
                JsonElement tagsEl = e.GetProperty("tags");
                var tags = new byte[tagsEl.GetArrayLength()][];
                int t = 0;
                foreach (JsonElement tag in tagsEl.EnumerateArray())
                    tags[t++] = Encoding.UTF8.GetBytes(tag.GetString() ?? "");
                items[i++] = new Item(
                    e.GetProperty("id").GetInt64(),
                    Encoding.UTF8.GetBytes(e.GetProperty("name").GetString() ?? ""),
                    Encoding.UTF8.GetBytes(e.GetProperty("category").GetString() ?? ""),
                    e.GetProperty("price").GetInt64(),
                    e.GetProperty("quantity").GetInt64(),
                    e.GetProperty("active").GetBoolean(),
                    tags,
                    rating.GetProperty("score").GetInt64(),
                    rating.GetProperty("count").GetInt64());
            }
            return new Dataset(items);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rhythm] dataset load failed ({path}): {ex.Message}");
            return Empty;
        }
    }
}
