using System.Collections.Concurrent;
using Azure.Data.Tables;
using DealsScannerPro.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Services;

public class CategoryService : ICategoryService
{
    private readonly TableClient _categoriesTable;
    private readonly ILogger<CategoryService> _logger;

    // In-memory cache with expiration
    private static List<Category>? _cachedCategories;
    private static DateTime _cacheExpiration = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly object _cacheLock = new();

    public CategoryService(IConfiguration configuration, ILogger<CategoryService> logger)
    {
        _logger = logger;

        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);
        _categoriesTable = serviceClient.GetTableClient("Categories");
        _categoriesTable.CreateIfNotExists();
    }

    public async Task<List<Category>> GetCategoriesAsync(bool includeInactive = false)
    {
        // Check cache first
        lock (_cacheLock)
        {
            if (_cachedCategories != null && DateTime.UtcNow < _cacheExpiration)
            {
                return includeInactive
                    ? _cachedCategories
                    : _cachedCategories.Where(c => c.Active).ToList();
            }
        }

        // Fetch from Table Storage
        var categories = new List<Category>();
        await foreach (var entity in _categoriesTable.QueryAsync<Category>(c => c.PartitionKey == "categories"))
        {
            categories.Add(entity);
        }

        // If no categories exist, seed defaults
        if (categories.Count == 0)
        {
            _logger.LogInformation("No categories found, seeding defaults...");
            await SeedDefaultCategoriesAsync();
            categories = await FetchCategoriesFromStorageAsync();
        }

        // Update cache
        lock (_cacheLock)
        {
            _cachedCategories = categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToList();
            _cacheExpiration = DateTime.UtcNow.Add(CacheDuration);
        }

        _logger.LogDebug("Loaded {Count} categories from storage", categories.Count);

        return includeInactive
            ? categories
            : categories.Where(c => c.Active).ToList();
    }

    private async Task<List<Category>> FetchCategoriesFromStorageAsync()
    {
        var categories = new List<Category>();
        await foreach (var entity in _categoriesTable.QueryAsync<Category>(c => c.PartitionKey == "categories"))
        {
            categories.Add(entity);
        }
        return categories;
    }

    public async Task<Category?> GetCategoryAsync(string categoryId)
    {
        try
        {
            var response = await _categoriesTable.GetEntityAsync<Category>("categories", categoryId.ToLowerInvariant());
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<Category> UpsertCategoryAsync(Category category)
    {
        category.PartitionKey = "categories";
        category.RowKey = category.RowKey.ToLowerInvariant();

        await _categoriesTable.UpsertEntityAsync(category, TableUpdateMode.Replace);
        ClearCache();

        _logger.LogInformation("Upserted category: {CategoryId} ({Name})", category.RowKey, category.Name);
        return category;
    }

    public async Task DeleteCategoryAsync(string categoryId)
    {
        var category = await GetCategoryAsync(categoryId);
        if (category != null)
        {
            category.Active = false;
            await _categoriesTable.UpsertEntityAsync(category, TableUpdateMode.Replace);
            ClearCache();

            _logger.LogInformation("Soft-deleted category: {CategoryId}", categoryId);
        }
    }

    public async Task<string> ClassifyProductAsync(string productText)
    {
        if (string.IsNullOrWhiteSpace(productText))
            return "andet";

        var textLower = productText.ToLowerInvariant();
        var categories = await GetCategoriesAsync();

        // Score each category based on keyword matches
        var scores = new Dictionary<string, int>();

        foreach (var category in categories)
        {
            var keywords = category.GetKeywordList();
            var score = keywords.Count(keyword => textLower.Contains(keyword));

            if (score > 0)
            {
                scores[category.RowKey] = score;
            }
        }

        // Return category with highest score
        if (scores.Count > 0)
        {
            return scores.OrderByDescending(s => s.Value).First().Key;
        }

        return "andet";
    }

    public async Task<string> GetCategoriesForPromptAsync()
    {
        var categories = await GetCategoriesAsync();

        var lines = categories
            .Where(c => c.Active)
            .OrderBy(c => c.SortOrder)
            .Select(c =>
            {
                var desc = string.IsNullOrEmpty(c.Description) ? "" : $": {c.Description}";
                return $"   - {c.Name}{desc}";
            });

        return string.Join("\n", lines);
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cachedCategories = null;
            _cacheExpiration = DateTime.MinValue;
        }
        _logger.LogDebug("Category cache cleared");
    }

    public async Task SeedDefaultCategoriesAsync()
    {
        var defaultCategories = GetDefaultCategories();

        foreach (var category in defaultCategories)
        {
            try
            {
                // Only insert if doesn't exist
                try
                {
                    await _categoriesTable.GetEntityAsync<Category>("categories", category.RowKey);
                    _logger.LogDebug("Category {Id} already exists, skipping", category.RowKey);
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    await _categoriesTable.AddEntityAsync(category);
                    _logger.LogInformation("Seeded category: {Id} ({Name})", category.RowKey, category.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed category: {Id}", category.RowKey);
            }
        }

        ClearCache();
    }

    private static List<Category> GetDefaultCategories()
    {
        return new List<Category>
        {
            new()
            {
                RowKey = "mejeri",
                Name = "Mejeri",
                Description = "Mælk, ost, yoghurt, smør, fløde, skyr, æg",
                Keywords = "mælk,smør,ost,yoghurt,skyr,fløde,æg,lactofree,lurpak,arla,philadelphia,buko,cream,creme fraiche,kefir",
                SortOrder = 10,
                Icon = "🥛"
            },
            new()
            {
                RowKey = "koed",
                Name = "Kød",
                Description = "Kød, kylling, svinekød, oksekød, hakket kød, pølser",
                Keywords = "kylling,oksekød,svinekød,flæsk,bacon,pølse,hakket,steak,mørbrad,and,lam,kalv,medister,frikadelle,kød,lever,kotelet,filet,steg,bøf,farsbrød",
                SortOrder = 20,
                Icon = "🥩"
            },
            new()
            {
                RowKey = "paalæg",
                Name = "Pålæg",
                Description = "Leverpostej, spegepølse, skinke, pålægschokolade, smøreost",
                Keywords = "pålæg,skinke,salami,leverpostej,spegepølse,rullepølse,paté,pålægschokolade,smøreost,makrelsalat,kaviar,gårdleverpostej",
                SortOrder = 25,
                Icon = "🥪"
            },
            new()
            {
                RowKey = "fisk",
                Name = "Fisk",
                Description = "Frisk fisk, røget fisk, rejer, tun, makrel",
                Keywords = "laks,sild,rejer,torsk,makrel,tun,fisk,fiskefrikadeller,rogn,krabbe,hellefisk,rødspætte,muslinger",
                SortOrder = 30,
                Icon = "🐟"
            },
            new()
            {
                RowKey = "frugt-groent",
                Name = "Frugt & Grønt",
                Description = "Frugt, grøntsager, salat, kartofler",
                Keywords = "æble,appelsin,banan,tomat,agurk,salat,kartoffel,gulerod,løg,kål,ananas,clementiner,granatæble,pære,citron,avocado,melon,jordbær,hindbær,blåbær,svampe,champignon,squash,peberfrugt,broccoli,frugt,grønt,dadler",
                SortOrder = 40,
                Icon = "🥬"
            },
            new()
            {
                RowKey = "broed-bagvaerk",
                Name = "Brød & Bagværk",
                Description = "Brød, boller, kager, wienerbrød",
                Keywords = "brød,boller,rugbrød,toast,croissant,wienerbrød,kage,æbleskiver,bagværk,rundstykke,flute,ciabatta,baguette,knækbrød,franskbrød",
                SortOrder = 50,
                Icon = "🍞"
            },
            new()
            {
                RowKey = "drikkevarer",
                Name = "Drikkevarer",
                Description = "Sodavand, juice, vand, kaffe, te (ikke øl/vin)",
                Keywords = "cola,fanta,sprite,juice,vand,sodavand,kakao,pepsi,saft,kaffe,te,espresso,nescafe,merrild,karat",
                SortOrder = 60,
                Icon = "🥤"
            },
            new()
            {
                RowKey = "oel-vin",
                Name = "Øl & Vin",
                Description = "Øl, vin, spiritus, cider",
                Keywords = "øl,vin,carlsberg,tuborg,heineken,royal,whisky,whiskey,vodka,gin,rom,aquavit,likør,champagne,mousserende,cider,rødvin,hvidvin,rosé,pilsner,lager,ale",
                SortOrder = 65,
                Icon = "🍺"
            },
            new()
            {
                RowKey = "frost",
                Name = "Frost",
                Description = "Frosne varer, is, frossen pizza",
                Keywords = "is,frost,frossen,pizza,pommes,fritter,frosne,ispind,lasagne",
                SortOrder = 70,
                Icon = "🧊"
            },
            new()
            {
                RowKey = "kolonial",
                Name = "Kolonial",
                Description = "Konserves, pasta, ris, mel, sukker, krydderier, sauce",
                Keywords = "pasta,ris,mel,sukker,olie,eddike,sauce,ketchup,sennep,mayonnaise,remoulade,bouillon,krydderi,salt,peber,honning,marmelade,nutella,peanut,konserves,dåse,bønner,ærter",
                SortOrder = 80,
                Icon = "🥫"
            },
            new()
            {
                RowKey = "morgenmad",
                Name = "Morgenmad",
                Description = "Cornflakes, havregryn, müsli",
                Keywords = "cornflakes,havregryn,müsli,musli,granola,havrefras,cheerios,frosties,weetabix,morgenmad,cruesli,crunchy",
                SortOrder = 85,
                Icon = "🥣"
            },
            new()
            {
                RowKey = "snacks",
                Name = "Snacks",
                Description = "Chips, slik, chokolade, nødder, kiks",
                Keywords = "chips,slik,chokolade,nødder,popcorn,kiks,småkager,flødeboller,lakrids,vingummi,haribo,twist,pringles,cookie",
                SortOrder = 90,
                Icon = "🍿"
            },
            new()
            {
                RowKey = "personlig-pleje",
                Name = "Personlig pleje",
                Description = "Shampoo, tandpasta, creme, deodorant",
                Keywords = "shampoo,sæbe,tandpasta,deodorant,creme,showergel,hårpleje,bodylotion,balsam,barbering",
                SortOrder = 100,
                Icon = "🧴"
            },
            new()
            {
                RowKey = "rengoering",
                Name = "Rengøring",
                Description = "Opvaskemiddel, vaskemiddel, rengøringsmidler",
                Keywords = "vaskemiddel,opvask,rengøring,affald,skrald,poser,skyllemiddel,ajax,klorin",
                SortOrder = 110,
                Icon = "🧹"
            },
            new()
            {
                RowKey = "husholdning",
                Name = "Husholdning",
                Description = "Køkkenrulle, toiletpapir, folie, poser",
                Keywords = "toiletpapir,køkkenrulle,servietter,folie,frysepose,affaldspose,film,alu,lambi,zewa,papir",
                SortOrder = 115,
                Icon = "🧻"
            },
            new()
            {
                RowKey = "kaeledyr",
                Name = "Kæledyr",
                Description = "Hundefoder, kattefoder, dyreartikler",
                Keywords = "hundefoder,kattefoder,kattesand,hund,kat,pedigree,whiskas",
                SortOrder = 120,
                Icon = "🐕"
            },
            new()
            {
                RowKey = "baby",
                Name = "Baby",
                Description = "Bleer, babymos, babymad",
                Keywords = "ble,bleer,babymos,babymad,pampers,libero,baby",
                SortOrder = 125,
                Icon = "👶"
            },
            new()
            {
                RowKey = "non-food",
                Name = "Non-food",
                Description = "Tøj, sko, legetøj, elektronik, køkkenudstyr",
                Keywords = "tøj,sko,strømper,handsker,tæppe,legetøj,elektronik,køkken,lampe,lys,batteri,pude,dyne,sengetøj,spil,dukke",
                SortOrder = 130,
                Icon = "🎁"
            },
            new()
            {
                RowKey = "andet",
                Name = "Andet",
                Description = "Alt der ikke passer andre kategorier",
                Keywords = "",
                SortOrder = 999,
                Icon = "📦"
            }
        };
    }
}
