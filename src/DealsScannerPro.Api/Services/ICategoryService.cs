using DealsScannerPro.Api.Models;

namespace DealsScannerPro.Api.Services;

public interface ICategoryService
{
    /// <summary>
    /// Get all active categories, cached for performance.
    /// </summary>
    Task<List<Category>> GetCategoriesAsync(bool includeInactive = false);

    /// <summary>
    /// Get a single category by ID.
    /// </summary>
    Task<Category?> GetCategoryAsync(string categoryId);

    /// <summary>
    /// Create or update a category.
    /// </summary>
    Task<Category> UpsertCategoryAsync(Category category);

    /// <summary>
    /// Delete a category (soft delete - sets Active = false).
    /// </summary>
    Task DeleteCategoryAsync(string categoryId);

    /// <summary>
    /// Classify a product text into a category based on keywords.
    /// Returns the best matching category ID, or "andet" if no match.
    /// </summary>
    Task<string> ClassifyProductAsync(string productText);

    /// <summary>
    /// Get categories formatted for GPT prompt.
    /// </summary>
    Task<string> GetCategoriesForPromptAsync();

    /// <summary>
    /// Seed default categories if the table is empty.
    /// </summary>
    Task SeedDefaultCategoriesAsync();

    /// <summary>
    /// Clear the category cache (call after updates).
    /// </summary>
    void ClearCache();
}
