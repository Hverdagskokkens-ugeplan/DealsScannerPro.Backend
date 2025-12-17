"""
Category Service
================

Fetches product categories from the API and provides classification.
Includes local caching for performance.
"""

import os
import logging
import time
from typing import Dict, List, Optional
from dataclasses import dataclass, field

logger = logging.getLogger(__name__)


@dataclass
class Category:
    """Represents a product category."""
    id: str
    name: str
    keywords: List[str] = field(default_factory=list)
    description: Optional[str] = None
    sort_order: int = 100
    active: bool = True
    icon: Optional[str] = None


class CategoryService:
    """
    Service for fetching and using product categories.

    Categories are fetched from the API and cached locally.
    Falls back to hardcoded defaults if API is unavailable.
    """

    # Class-level cache
    _cache: Dict[str, Category] = {}
    _cache_timestamp: float = 0
    _cache_duration: float = 300  # 5 minutes

    def __init__(self, api_base_url: Optional[str] = None):
        """
        Initialize category service.

        Args:
            api_base_url: Base URL for the API (e.g., https://func-dealscanner-prod.azurewebsites.net)
        """
        self.api_base_url = api_base_url or os.environ.get(
            "API_BASE_URL",
            "https://func-dealscanner-prod.azurewebsites.net"
        )

    def get_categories(self, force_refresh: bool = False) -> Dict[str, Category]:
        """
        Get all active categories.

        Args:
            force_refresh: Force refresh from API even if cache is valid

        Returns:
            Dictionary of category_id -> Category
        """
        # Check cache
        if not force_refresh and self._is_cache_valid():
            return self._cache

        # Try to fetch from API
        try:
            categories = self._fetch_from_api()
            self._update_cache(categories)
            return self._cache
        except Exception as e:
            logger.warning(f"Failed to fetch categories from API: {e}")

            # Return cache if available, even if expired
            if self._cache:
                logger.info("Using expired cache")
                return self._cache

            # Fall back to defaults
            logger.info("Using default categories")
            defaults = self._get_default_categories()
            self._update_cache(defaults)
            return self._cache

    def _is_cache_valid(self) -> bool:
        """Check if cache is still valid."""
        if not self._cache:
            return False
        return time.time() - self._cache_timestamp < self._cache_duration

    def _update_cache(self, categories: List[Category]):
        """Update the cache with new categories."""
        CategoryService._cache = {cat.id: cat for cat in categories}
        CategoryService._cache_timestamp = time.time()
        logger.debug(f"Category cache updated with {len(categories)} categories")

    def _fetch_from_api(self) -> List[Category]:
        """Fetch categories from the API."""
        import requests

        url = f"{self.api_base_url}/api/categories"
        logger.debug(f"Fetching categories from {url}")

        response = requests.get(url, timeout=10)
        response.raise_for_status()

        data = response.json()
        categories = []

        for item in data.get("categories", []):
            categories.append(Category(
                id=item["id"],
                name=item["name"],
                keywords=item.get("keyword_list", []),
                description=item.get("description"),
                sort_order=item.get("sort_order", 100),
                active=item.get("active", True),
                icon=item.get("icon")
            ))

        logger.info(f"Fetched {len(categories)} categories from API")
        return categories

    def classify(self, product_text: str) -> str:
        """
        Classify a product into a category based on keywords.

        Args:
            product_text: Product text to classify

        Returns:
            Category ID (e.g., "mejeri", "koed", "andet")
        """
        if not product_text:
            return "andet"

        text_lower = product_text.lower()
        categories = self.get_categories()

        # Score each category
        scores: Dict[str, int] = {}

        for cat_id, category in categories.items():
            score = sum(1 for kw in category.keywords if kw in text_lower)
            if score > 0:
                scores[cat_id] = score

        # Return category with highest score
        if scores:
            return max(scores.items(), key=lambda x: x[1])[0]

        return "andet"

    def get_category_name(self, category_id: str) -> str:
        """Get the display name for a category ID."""
        categories = self.get_categories()
        category = categories.get(category_id)
        return category.name if category else "Andet"

    def get_prompt_text(self) -> str:
        """
        Get categories formatted for GPT prompt.

        Returns:
            Formatted string listing all categories with descriptions
        """
        categories = self.get_categories()

        lines = []
        for category in sorted(categories.values(), key=lambda c: c.sort_order):
            if category.active:
                desc = f": {category.description}" if category.description else ""
                lines.append(f"   - {category.name}{desc}")

        return "\n".join(lines)

    def get_keywords_dict(self) -> Dict[str, List[str]]:
        """
        Get categories as a dictionary of name -> keywords.
        Compatible with existing scanner format.

        Returns:
            Dictionary like {'Mejeri': ['mælk', 'ost', ...], ...}
        """
        categories = self.get_categories()
        return {
            cat.name: cat.keywords
            for cat in categories.values()
            if cat.active
        }

    def clear_cache(self):
        """Clear the category cache."""
        CategoryService._cache = {}
        CategoryService._cache_timestamp = 0
        logger.info("Category cache cleared")

    def _get_default_categories(self) -> List[Category]:
        """Get hardcoded default categories as fallback."""
        return [
            Category(
                id="mejeri", name="Mejeri",
                keywords=["mælk", "smør", "ost", "yoghurt", "skyr", "fløde", "æg", "arla", "lurpak"],
                description="Mælk, ost, yoghurt, smør, fløde, skyr, æg",
                sort_order=10
            ),
            Category(
                id="koed", name="Kød",
                keywords=["kylling", "oksekød", "svinekød", "flæsk", "bacon", "pølse", "hakket", "kød", "medister"],
                description="Kød, kylling, svinekød, oksekød, hakket kød, pølser",
                sort_order=20
            ),
            Category(
                id="paalæg", name="Pålæg",
                keywords=["pålæg", "skinke", "salami", "leverpostej", "spegepølse", "rullepølse"],
                description="Leverpostej, spegepølse, skinke",
                sort_order=25
            ),
            Category(
                id="fisk", name="Fisk",
                keywords=["laks", "sild", "rejer", "torsk", "makrel", "tun", "fisk"],
                description="Frisk fisk, røget fisk, rejer, tun, makrel",
                sort_order=30
            ),
            Category(
                id="frugt-groent", name="Frugt & Grønt",
                keywords=["æble", "banan", "tomat", "agurk", "salat", "kartoffel", "gulerod", "frugt", "grønt"],
                description="Frugt, grøntsager, salat, kartofler",
                sort_order=40
            ),
            Category(
                id="broed-bagvaerk", name="Brød & Bagværk",
                keywords=["brød", "boller", "rugbrød", "toast", "croissant", "kage"],
                description="Brød, boller, kager",
                sort_order=50
            ),
            Category(
                id="drikkevarer", name="Drikkevarer",
                keywords=["cola", "juice", "vand", "sodavand", "kaffe", "te"],
                description="Sodavand, juice, vand, kaffe, te",
                sort_order=60
            ),
            Category(
                id="oel-vin", name="Øl & Vin",
                keywords=["øl", "vin", "carlsberg", "tuborg", "whisky", "vodka", "champagne"],
                description="Øl, vin, spiritus",
                sort_order=65
            ),
            Category(
                id="frost", name="Frost",
                keywords=["is", "frost", "frossen", "pizza", "frosne"],
                description="Frosne varer, is, frossen pizza",
                sort_order=70
            ),
            Category(
                id="kolonial", name="Kolonial",
                keywords=["pasta", "ris", "mel", "sukker", "sauce", "ketchup", "konserves"],
                description="Konserves, pasta, ris, sauce",
                sort_order=80
            ),
            Category(
                id="snacks", name="Snacks",
                keywords=["chips", "slik", "chokolade", "nødder", "popcorn", "kiks"],
                description="Chips, slik, chokolade, nødder",
                sort_order=90
            ),
            Category(
                id="personlig-pleje", name="Personlig pleje",
                keywords=["shampoo", "sæbe", "tandpasta", "deodorant", "creme"],
                description="Shampoo, tandpasta, creme",
                sort_order=100
            ),
            Category(
                id="rengoering", name="Rengøring",
                keywords=["vaskemiddel", "opvask", "rengøring"],
                description="Opvaskemiddel, vaskemiddel",
                sort_order=110
            ),
            Category(
                id="husholdning", name="Husholdning",
                keywords=["toiletpapir", "køkkenrulle", "servietter", "folie"],
                description="Køkkenrulle, toiletpapir, folie",
                sort_order=115
            ),
            Category(
                id="non-food", name="Non-food",
                keywords=["tøj", "sko", "legetøj", "elektronik"],
                description="Tøj, sko, legetøj, elektronik",
                sort_order=130
            ),
            Category(
                id="andet", name="Andet",
                keywords=[],
                description="Alt der ikke passer andre kategorier",
                sort_order=999
            ),
        ]


# Global instance for convenience
_default_service: Optional[CategoryService] = None


def get_category_service(api_base_url: Optional[str] = None) -> CategoryService:
    """Get or create the default category service instance."""
    global _default_service

    if _default_service is None:
        _default_service = CategoryService(api_base_url)

    return _default_service
