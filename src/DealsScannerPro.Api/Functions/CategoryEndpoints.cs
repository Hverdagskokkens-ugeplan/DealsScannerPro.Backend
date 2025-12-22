using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Models;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

/// <summary>
/// API endpoints for managing product categories.
/// Categories are stored in Azure Table Storage and cached for performance.
/// </summary>
public class CategoryEndpoints
{
    private readonly ICategoryService _categoryService;
    private readonly ILogger<CategoryEndpoints> _logger;

    public CategoryEndpoints(ICategoryService categoryService, ILogger<CategoryEndpoints> logger)
    {
        _categoryService = categoryService;
        _logger = logger;
    }

    /// <summary>
    /// Get all categories.
    /// GET /api/categories?includeInactive=false
    /// </summary>
    [Function("GetCategories")]
    public async Task<HttpResponseData> GetCategories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "categories")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var includeInactive = query["includeInactive"] == "true";

            var categories = await _categoryService.GetCategoriesAsync(includeInactive);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                categories = categories.Select(c => MapCategoryToResponse(c)),
                count = categories.Count
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting categories");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Get a single category by ID.
    /// GET /api/categories/{id}
    /// </summary>
    [Function("GetCategory")]
    public async Task<HttpResponseData> GetCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "categories/{id}")] HttpRequestData req,
        string id)
    {
        try
        {
            var category = await _categoryService.GetCategoryAsync(id);

            if (category == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Category '{id}' not found" });
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(MapCategoryToResponse(category));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting category {Id}", id);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Create or update a category.
    /// POST /api/categories
    /// </summary>
    [Function("UpsertCategory")]
    public async Task<HttpResponseData> UpsertCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "categories")] HttpRequestData req)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<CategoryRequest>();

            if (request == null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Name))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "id and name are required" });
                return badRequest;
            }

            var category = new Category
            {
                RowKey = request.Id.ToLowerInvariant(),
                Name = request.Name,
                Keywords = request.Keywords ?? "",
                Description = request.Description,
                SortOrder = request.SortOrder ?? 100,
                Active = request.Active ?? true,
                ParentCategoryId = request.ParentCategoryId,
                Icon = request.Icon
            };

            var saved = await _categoryService.UpsertCategoryAsync(category);

            _logger.LogInformation("Category upserted: {Id} ({Name})", saved.RowKey, saved.Name);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                category = MapCategoryToResponse(saved)
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting category");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Delete a category (soft delete).
    /// DELETE /api/categories/{id}
    /// </summary>
    [Function("DeleteCategory")]
    public async Task<HttpResponseData> DeleteCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "categories/{id}")] HttpRequestData req,
        string id)
    {
        try
        {
            await _categoryService.DeleteCategoryAsync(id);

            _logger.LogInformation("Category deleted: {Id}", id);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, message = $"Category '{id}' deleted" });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting category {Id}", id);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Seed default categories.
    /// POST /api/management/seed-categories
    /// </summary>
    [Function("SeedCategories")]
    public async Task<HttpResponseData> SeedCategories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "management/seed-categories")] HttpRequestData req)
    {
        try
        {
            await _categoryService.SeedDefaultCategoriesAsync();

            var categories = await _categoryService.GetCategoriesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                message = "Categories seeded successfully",
                count = categories.Count
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding categories");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Get categories formatted for GPT prompt.
    /// GET /api/categories/prompt
    /// </summary>
    [Function("GetCategoriesForPrompt")]
    public async Task<HttpResponseData> GetCategoriesForPrompt(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "categories/prompt")] HttpRequestData req)
    {
        try
        {
            var prompt = await _categoryService.GetCategoriesForPromptAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { prompt });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting categories for prompt");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Classify a product text into a category.
    /// POST /api/categories/classify
    /// </summary>
    [Function("ClassifyProduct")]
    public async Task<HttpResponseData> ClassifyProduct(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "categories/classify")] HttpRequestData req)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<ClassifyRequest>();

            if (request == null || string.IsNullOrWhiteSpace(request.ProductText))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "product_text is required" });
                return badRequest;
            }

            var categoryId = await _categoryService.ClassifyProductAsync(request.ProductText);
            var category = await _categoryService.GetCategoryAsync(categoryId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                category_id = categoryId,
                category_name = category?.Name ?? "Andet",
                product_text = request.ProductText
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error classifying product");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Clear category cache.
    /// POST /api/management/clear-category-cache
    /// </summary>
    [Function("ClearCategoryCache")]
    public HttpResponseData ClearCache(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "management/clear-category-cache")] HttpRequestData req)
    {
        _categoryService.ClearCache();

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.WriteAsJsonAsync(new { success = true, message = "Category cache cleared" });
        return response;
    }

    private static object MapCategoryToResponse(Category c)
    {
        return new
        {
            id = c.RowKey,
            name = c.Name,
            keywords = c.Keywords,
            keyword_list = c.GetKeywordList(),
            description = c.Description,
            sort_order = c.SortOrder,
            active = c.Active,
            parent_category_id = c.ParentCategoryId,
            icon = c.Icon
        };
    }
}

// Request models
public class CategoryRequest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Keywords { get; set; }
    public string? Description { get; set; }
    public int? SortOrder { get; set; }
    public bool? Active { get; set; }
    public string? ParentCategoryId { get; set; }
    public string? Icon { get; set; }
}

public class ClassifyRequest
{
    public string ProductText { get; set; } = string.Empty;
}
