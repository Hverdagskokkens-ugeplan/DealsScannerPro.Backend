"""
OpenAI Normalizer Service
=========================

Uses Azure OpenAI GPT-4o-mini to normalize product information
extracted from supermarket flyers.

Extracts:
- brand_norm: Brand name (Coca-Cola, Arla, etc.)
- product_norm: Generic product name (Cola, Mælk, etc.)
- variant_norm: Product variant (Zero, Light, Økologisk, etc.)
- category: Product category (loaded dynamically from API)
- net_amount_value/unit: Package size (500, ml)
- pack_count: Number of items (6 for 6-pack)
- container_type: CAN, BOTTLE, BAG, TRAY, BOX, JAR
- deposit_value: Pant amount if mentioned
- comment: Restrictions or special conditions
"""

import os
import json
import logging
from typing import Optional, List, Dict
from dataclasses import dataclass

logger = logging.getLogger(__name__)

# Import category service (lazy loaded to avoid circular imports)
_category_service = None

def _get_category_service():
    """Get or create the category service (lazy loading)."""
    global _category_service
    if _category_service is None:
        try:
            from .category_service import get_category_service
            _category_service = get_category_service()
        except Exception as e:
            logger.warning(f"Could not load category service: {e}")
    return _category_service


@dataclass
class NormalizedProduct:
    """Normalized product information from GPT."""
    brand_norm: Optional[str] = None
    product_norm: Optional[str] = None
    variant_norm: Optional[str] = None
    category: str = "Andet"
    net_amount_value: Optional[float] = None
    net_amount_unit: Optional[str] = None
    pack_count: Optional[int] = None
    container_type: Optional[str] = None
    deposit_value: Optional[float] = None
    comment: Optional[str] = None
    confidence: float = 0.5


# Default categories (fallback if service unavailable)
DEFAULT_CATEGORIES = [
    "Mejeri", "Kød", "Pålæg", "Fisk", "Frugt & Grønt", "Brød & Bagværk",
    "Drikkevarer", "Øl & Vin", "Frost", "Kolonial", "Morgenmad", "Snacks",
    "Personlig pleje", "Rengøring", "Husholdning", "Kæledyr", "Baby", "Non-food", "Andet"
]


def get_categories() -> List[str]:
    """Get list of valid category names from the category service."""
    service = _get_category_service()
    if service:
        try:
            categories = service.get_categories()
            return [cat.name for cat in categories.values() if cat.active]
        except Exception as e:
            logger.warning(f"Failed to get categories from service: {e}")
    return DEFAULT_CATEGORIES


def get_category_keywords() -> Dict[str, List[str]]:
    """Get category keywords dict from the category service."""
    service = _get_category_service()
    if service:
        try:
            return service.get_keywords_dict()
        except Exception as e:
            logger.warning(f"Failed to get category keywords: {e}")
    return {}


# Container types
CONTAINER_TYPES = ["CAN", "BOTTLE", "BAG", "TRAY", "BOX", "JAR", "TUBE", "NONE"]

# System prompt for GPT
SYSTEM_PROMPT = """Du er en ekspert i at analysere produkttekst fra danske supermarkedstilbud.

Din opgave er at normalisere produktinformation til strukturerede felter.

REGLER:
1. brand_norm: Varemærket (fx "Coca-Cola", "Arla", "Kellogg's", "Tulip"). Null hvis intet brand/private label.
2. product_norm: Det generiske produktnavn på dansk (fx "Cola", "Letmælk", "Cornflakes"). Altid udfyldt.
3. variant_norm: Varianten (fx "Zero", "Økologisk", "Original", "Med nødder", "Grovhakket"). Null hvis ingen variant.
4. category: En af disse kategorier:
   - Mejeri: Mælk, ost, yoghurt, smør, fløde, skyr
   - Kød: Kød, kylling, svinekød, oksekød, hakket kød, pølser
   - Pålæg: Leverpostej, spegepølse, skinke, pålægschokolade, smøreost
   - Fisk: Frisk fisk, røget fisk, rejer, tun, makrel
   - Frugt & Grønt: Frugt, grøntsager, salat, kartofler
   - Brød & Bagværk: Brød, boller, kager, wienerbrød
   - Drikkevarer: Sodavand, juice, vand, kaffe, te (IKKE øl/vin)
   - Øl & Vin: Øl, vin, cider, alkohol
   - Frost: Frosne varer, is, frossen pizza
   - Kolonial: Konserves, pasta, ris, mel, sukker, krydderier, sauce
   - Morgenmad: Cornflakes, havregryn, müsli, morgenmadsprodukter
   - Snacks: Chips, slik, chokolade, nødder, popcorn, kiks
   - Personlig pleje: Shampoo, tandpasta, creme, deodorant
   - Rengøring: Opvaskemiddel, vaskemiddel, rengøringsmidler
   - Kæledyr: Hundefoder, kattefoder, dyreartikler
   - Baby: Bleer, babymos, babymad
   - Husholdning: Køkkenrulle, toiletpapir, folie, poser
   - Andet: Alt der ikke passer andre kategorier
5. net_amount_value: Talværdi for mængde (fx 500 for "500g"). Konverter cl→ml (33cl=330ml). Null hvis ukendt.
6. net_amount_unit: Enhed (ml, g, kg, l, stk). Standardiser: gram→g, liter→l, kilo→kg, cl→ml
7. pack_count: Antal i pakke (fx 6 for "6-pak" eller "6 x 33cl"). Null hvis enkelt produkt.
8. container_type: En af: CAN (dåse), BOTTLE (flaske), BAG (pose), TRAY (bakke), BOX (æske), JAR (glas), TUBE (tube), NONE (ingen/ukendt)
9. deposit_value: Pantværdi i kr hvis nævnt (1, 1.5, eller 3). Null hvis ingen pant nævnt eksplicit.
10. comment: Restriktioner eller bemærkninger (fx "Max 3 stk", "Kun med medlemskort"). Null hvis ingen.

VIGTIGE REGLER:
- "Øko" eller "Økologisk" → variant_norm, IKKE brand
- "Dansk" → variant_norm, IKKE brand
- Private label (ingen brand) → brand_norm = null
- Multi-buy ("2 for 30kr") → comment: "2 for 30 kr"
- Kilopris info → ignorer, det er reference
- Pålæg i skiver → category: "Pålæg", container_type: "TRAY"

EKSEMPLER:

Input: "Coca-Cola Zero 6-pak 33 cl dåser"
Output: {"brand_norm": "Coca-Cola", "product_norm": "Cola", "variant_norm": "Zero", "category": "Drikkevarer", "net_amount_value": 330, "net_amount_unit": "ml", "pack_count": 6, "container_type": "CAN", "deposit_value": null, "comment": null}

Input: "Arla Lærkevang Øko Letmælk 1 L"
Output: {"brand_norm": "Arla", "product_norm": "Letmælk", "variant_norm": "Økologisk Lærkevang", "category": "Mejeri", "net_amount_value": 1000, "net_amount_unit": "ml", "pack_count": null, "container_type": "BOTTLE", "deposit_value": null, "comment": null}

Input: "Dansk hakket oksekød 8-12% 500g"
Output: {"brand_norm": null, "product_norm": "Hakket oksekød", "variant_norm": "Dansk 8-12% fedt", "category": "Kød", "net_amount_value": 500, "net_amount_unit": "g", "pack_count": null, "container_type": "TRAY", "deposit_value": null, "comment": null}

Input: "Tulip Leverpostej 350g"
Output: {"brand_norm": "Tulip", "product_norm": "Leverpostej", "variant_norm": null, "category": "Pålæg", "net_amount_value": 350, "net_amount_unit": "g", "pack_count": null, "container_type": "TRAY", "deposit_value": null, "comment": null}

Input: "Kellogg's Corn Flakes 500g"
Output: {"brand_norm": "Kellogg's", "product_norm": "Cornflakes", "variant_norm": null, "category": "Morgenmad", "net_amount_value": 500, "net_amount_unit": "g", "pack_count": null, "container_type": "BOX", "deposit_value": null, "comment": null}

Input: "Tuborg Classic 6-pak 33cl dåser + pant"
Output: {"brand_norm": "Tuborg", "product_norm": "Øl", "variant_norm": "Classic", "category": "Øl & Vin", "net_amount_value": 330, "net_amount_unit": "ml", "pack_count": 6, "container_type": "CAN", "deposit_value": 1, "comment": null}

Input: "Lambi Toiletpapir 24 ruller"
Output: {"brand_norm": "Lambi", "product_norm": "Toiletpapir", "variant_norm": null, "category": "Husholdning", "net_amount_value": null, "net_amount_unit": null, "pack_count": 24, "container_type": null, "deposit_value": null, "comment": null}

Input: "Grøntsagsmix til wok 300g frost"
Output: {"brand_norm": null, "product_norm": "Grøntsagsmix", "variant_norm": "Wok", "category": "Frost", "net_amount_value": 300, "net_amount_unit": "g", "pack_count": null, "container_type": "BAG", "deposit_value": null, "comment": null}

Input: "Pringles Original 165g Max 3 pr. kunde"
Output: {"brand_norm": "Pringles", "product_norm": "Chips", "variant_norm": "Original", "category": "Snacks", "net_amount_value": 165, "net_amount_unit": "g", "pack_count": null, "container_type": "TUBE", "deposit_value": null, "comment": "Max 3 pr. kunde"}

Input: "Økologiske æbler 1 kg"
Output: {"brand_norm": null, "product_norm": "Æbler", "variant_norm": "Økologisk", "category": "Frugt & Grønt", "net_amount_value": 1000, "net_amount_unit": "g", "pack_count": null, "container_type": "BAG", "deposit_value": null, "comment": null}

Input: "2 stk. Dansk Kyllingebryst 2 for 50 kr"
Output: {"brand_norm": null, "product_norm": "Kyllingebryst", "variant_norm": "Dansk", "category": "Kød", "net_amount_value": null, "net_amount_unit": null, "pack_count": 2, "container_type": "TRAY", "deposit_value": null, "comment": "2 for 50 kr"}

Returner KUN valid JSON. Ingen forklaring."""


class OpenAINormalizer:
    """
    Service for normalizing product information using GPT-4o-mini.

    Features:
    - GPT-4o-mini normalization with fallback to rule-based
    - Supports both standard OpenAI API and Azure OpenAI
    - In-memory caching to avoid redundant API calls
    - Batch processing support for efficiency
    """

    # Class-level cache (shared across instances within same process)
    _cache: dict = {}
    _cache_hits: int = 0
    _cache_misses: int = 0

    def __init__(
        self,
        api_key: Optional[str] = None,
        endpoint: Optional[str] = None,
        model_name: str = "gpt-4o-mini",
        enable_cache: bool = True,
        max_cache_size: int = 1000
    ):
        """
        Initialize OpenAI normalizer.

        Supports both standard OpenAI API and Azure OpenAI.
        - For standard OpenAI: Set OPENAI_API_KEY env var
        - For Azure OpenAI: Set AZURE_OPENAI_API_KEY and AZURE_OPENAI_ENDPOINT

        Args:
            api_key: OpenAI API key (checks OPENAI_API_KEY, then AZURE_OPENAI_API_KEY)
            endpoint: Azure OpenAI endpoint (only needed for Azure)
            model_name: Model name (gpt-4o-mini)
            enable_cache: Enable in-memory caching of normalized products
            max_cache_size: Maximum number of cached products
        """
        # Check for standard OpenAI first, then Azure OpenAI
        self.api_key = api_key or os.environ.get("OPENAI_API_KEY") or os.environ.get("AZURE_OPENAI_API_KEY")
        self.endpoint = endpoint or os.environ.get("AZURE_OPENAI_ENDPOINT")
        self.model_name = model_name
        self.enable_cache = enable_cache
        self.max_cache_size = max_cache_size

        # Determine which API to use
        self.use_azure = bool(self.endpoint and os.environ.get("AZURE_OPENAI_API_KEY"))

        self._client = None

        if not self.api_key:
            logger.warning(
                "OpenAI credentials not configured. "
                "Set OPENAI_API_KEY for standard API or "
                "AZURE_OPENAI_API_KEY and AZURE_OPENAI_ENDPOINT for Azure. "
                "Falling back to rule-based normalization."
            )
        else:
            api_type = "Azure OpenAI" if self.use_azure else "OpenAI"
            logger.info(f"OpenAI normalizer configured with {api_type}")

    def _get_cache_key(self, product_text: str, price: Optional[float] = None) -> str:
        """Generate cache key from product text and price."""
        # Normalize the text for better cache hits
        normalized_text = product_text.strip().lower()
        if price:
            return f"{normalized_text}|{price:.2f}"
        return normalized_text

    def _get_from_cache(self, cache_key: str) -> Optional[NormalizedProduct]:
        """Get product from cache if available."""
        if not self.enable_cache:
            return None
        result = self._cache.get(cache_key)
        if result:
            OpenAINormalizer._cache_hits += 1
            logger.debug(f"Cache hit for: {cache_key[:50]}...")
        return result

    def _add_to_cache(self, cache_key: str, product: NormalizedProduct):
        """Add product to cache."""
        if not self.enable_cache:
            return
        # Simple LRU-like: clear half the cache when full
        if len(self._cache) >= self.max_cache_size:
            keys_to_remove = list(self._cache.keys())[:self.max_cache_size // 2]
            for key in keys_to_remove:
                del self._cache[key]
            logger.debug(f"Cache trimmed to {len(self._cache)} items")
        self._cache[cache_key] = product
        OpenAINormalizer._cache_misses += 1

    @classmethod
    def get_cache_stats(cls) -> dict:
        """Get cache statistics."""
        total = cls._cache_hits + cls._cache_misses
        hit_rate = cls._cache_hits / total if total > 0 else 0
        return {
            "hits": cls._cache_hits,
            "misses": cls._cache_misses,
            "size": len(cls._cache),
            "hit_rate": f"{hit_rate:.1%}"
        }

    @classmethod
    def clear_cache(cls):
        """Clear the cache."""
        cls._cache.clear()
        cls._cache_hits = 0
        cls._cache_misses = 0
        logger.info("Cache cleared")

    def _get_client(self):
        """Lazy initialization of OpenAI client."""
        if self._client is None:
            if not self.api_key:
                return None

            if self.use_azure:
                # Azure OpenAI
                from openai import AzureOpenAI
                self._client = AzureOpenAI(
                    api_key=self.api_key,
                    api_version="2024-02-15-preview",
                    azure_endpoint=self.endpoint
                )
            else:
                # Standard OpenAI API
                from openai import OpenAI
                self._client = OpenAI(api_key=self.api_key)

        return self._client

    def normalize(
        self,
        product_text: str,
        price: Optional[float] = None,
        additional_context: Optional[str] = None
    ) -> NormalizedProduct:
        """
        Normalize product text using GPT.

        Args:
            product_text: Raw product text from flyer
            price: Optional price for context
            additional_context: Optional additional text (e.g., unit price line)

        Returns:
            NormalizedProduct with extracted fields
        """
        # Check cache first
        cache_key = self._get_cache_key(product_text, price)
        cached = self._get_from_cache(cache_key)
        if cached:
            return cached

        result = None

        # Try GPT first
        client = self._get_client()
        if client:
            try:
                result = self._normalize_with_gpt(
                    client, product_text, price, additional_context
                )
            except Exception as e:
                logger.warning(f"GPT normalization failed: {e}, using fallback")

        # Fallback to rule-based
        if result is None:
            result = self._normalize_with_rules(product_text)

        # Cache the result
        self._add_to_cache(cache_key, result)

        return result

    def _normalize_with_gpt(
        self,
        client,
        product_text: str,
        price: Optional[float],
        additional_context: Optional[str]
    ) -> NormalizedProduct:
        """Normalize using GPT-4o-mini."""

        # Build user message
        user_message = f"Produkt: {product_text}"
        if price:
            user_message += f"\nPris: {price} kr"
        if additional_context:
            user_message += f"\nEkstra: {additional_context}"

        response = client.chat.completions.create(
            model=self.model_name,
            messages=[
                {"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user", "content": user_message}
            ],
            temperature=0.1,  # Low temperature for consistency
            max_tokens=300,
            response_format={"type": "json_object"}
        )

        # Parse response
        content = response.choices[0].message.content
        data = json.loads(content)

        # Validate and convert
        return NormalizedProduct(
            brand_norm=self._clean_string(data.get("brand_norm")),
            product_norm=self._clean_string(data.get("product_norm")) or product_text,
            variant_norm=self._clean_string(data.get("variant_norm")),
            category=self._validate_category(data.get("category")),
            net_amount_value=self._to_float(data.get("net_amount_value")),
            net_amount_unit=self._normalize_unit(data.get("net_amount_unit")),
            pack_count=self._to_int(data.get("pack_count")),
            container_type=self._validate_container(data.get("container_type")),
            deposit_value=self._to_float(data.get("deposit_value")),
            comment=self._clean_string(data.get("comment")),
            confidence=0.9  # High confidence for GPT
        )

    def _normalize_with_rules(self, product_text: str) -> NormalizedProduct:
        """Fallback rule-based normalization."""
        import re

        result = NormalizedProduct(
            product_norm=product_text,
            confidence=0.5
        )

        text_lower = product_text.lower()

        # Try to extract brand (first capitalized word)
        brand_match = re.match(r'^([A-ZÆØÅ][a-zæøå]+(?:\s+[A-ZÆØÅ][a-zæøå]+)?)', product_text)
        if brand_match:
            potential_brand = brand_match.group(1)
            # Check if it's a known brand pattern (not generic words)
            if potential_brand.lower() not in ['dansk', 'økologisk', 'frisk', 'god', 'lækker']:
                result.brand_norm = potential_brand

        # Try to extract amount
        amount_match = re.search(
            r'(\d+(?:[.,]\d+)?)\s*(g|kg|ml|cl|dl|l|liter|stk)\b',
            text_lower
        )
        if amount_match:
            result.net_amount_value = float(amount_match.group(1).replace(',', '.'))
            result.net_amount_unit = self._normalize_unit(amount_match.group(2))

        # Try to extract pack count
        pack_match = re.search(r'(\d+)\s*(?:x|-pak|pak|stk)', text_lower)
        if pack_match:
            count = int(pack_match.group(1))
            if 2 <= count <= 24:  # Reasonable pack size
                result.pack_count = count

        # Detect container type
        result.container_type = self._detect_container(text_lower)

        # Detect category
        result.category = self._detect_category(text_lower)

        # Detect comments
        comment_match = re.search(r'(max\.?\s*\d+\s*(?:stk|pr|per).*?)(?:\.|$)', text_lower)
        if comment_match:
            result.comment = comment_match.group(1).strip()

        return result

    def _clean_string(self, value: Optional[str]) -> Optional[str]:
        """Clean and validate string value."""
        if value is None:
            return None
        if isinstance(value, str):
            cleaned = value.strip()
            return cleaned if cleaned and cleaned.lower() != 'null' else None
        return None

    def _to_float(self, value) -> Optional[float]:
        """Convert to float safely."""
        if value is None:
            return None
        try:
            return float(value)
        except (ValueError, TypeError):
            return None

    def _to_int(self, value) -> Optional[int]:
        """Convert to int safely."""
        if value is None:
            return None
        try:
            return int(value)
        except (ValueError, TypeError):
            return None

    def _normalize_unit(self, unit: Optional[str]) -> Optional[str]:
        """Normalize unit to standard form."""
        if not unit:
            return None

        unit = unit.lower().strip()

        mapping = {
            'gram': 'g',
            'kilo': 'kg',
            'kilogram': 'kg',
            'liter': 'l',
            'centiliter': 'cl',
            'deciliter': 'dl',
            'milliliter': 'ml',
            'styk': 'stk',
            'stykker': 'stk',
            'pakke': 'stk',
        }

        return mapping.get(unit, unit)

    def _validate_category(self, category: Optional[str]) -> str:
        """Validate category against allowed list (dynamic from API)."""
        if not category:
            return "Andet"

        # Get valid categories from service
        valid_categories = get_categories()

        # Find closest match (case-insensitive)
        category_lower = category.lower()
        for valid_cat in valid_categories:
            if valid_cat.lower() == category_lower:
                return valid_cat

        return "Andet"

    def _validate_container(self, container: Optional[str]) -> Optional[str]:
        """Validate container type."""
        if not container:
            return None

        container_upper = container.upper()
        if container_upper in CONTAINER_TYPES:
            return container_upper if container_upper != "NONE" else None

        return None

    def _detect_container(self, text: str) -> Optional[str]:
        """Detect container type from text."""
        text = text.lower()

        if any(w in text for w in ['dåse', 'dåser', 'can']):
            return 'CAN'
        if any(w in text for w in ['flaske', 'flasker', 'pet', 'glas']):
            return 'BOTTLE'
        if any(w in text for w in ['pose', 'poser', 'bag']):
            return 'BAG'
        if any(w in text for w in ['bakke', 'bakker', 'tray']):
            return 'TRAY'
        if any(w in text for w in ['æske', 'karton', 'box', 'pakke']):
            return 'BOX'
        if any(w in text for w in ['glas', 'jar', 'syltetøj']):
            return 'JAR'
        if any(w in text for w in ['tube']):
            return 'TUBE'

        return None

    def _detect_category(self, text: str) -> str:
        """Detect category from text using dynamic keywords from API."""
        if not text:
            return 'Andet'

        text_lower = text.lower()

        # Get keywords from category service
        category_keywords = get_category_keywords()

        # If service unavailable, use fallback detection
        if not category_keywords:
            return self._detect_category_fallback(text_lower)

        # Score each category based on keyword matches
        scores: Dict[str, int] = {}
        for category_name, keywords in category_keywords.items():
            score = sum(1 for kw in keywords if kw in text_lower)
            if score > 0:
                scores[category_name] = score

        # Return category with highest score
        if scores:
            return max(scores.items(), key=lambda x: x[1])[0]

        return 'Andet'

    def _detect_category_fallback(self, text: str) -> str:
        """Fallback category detection when service is unavailable."""
        # Simplified fallback with most common categories
        fallback_keywords = {
            'Øl & Vin': ['øl', 'vin', 'carlsberg', 'tuborg', 'whisky', 'champagne'],
            'Drikkevarer': ['cola', 'sodavand', 'juice', 'kaffe', 'te'],
            'Mejeri': ['mælk', 'ost', 'yoghurt', 'smør', 'fløde', 'skyr'],
            'Pålæg': ['leverpostej', 'spegepølse', 'skinke', 'pålæg'],
            'Kød': ['kød', 'kylling', 'svin', 'okse', 'hakket', 'bacon'],
            'Fisk': ['fisk', 'laks', 'torsk', 'reje', 'tun', 'sild'],
            'Frugt & Grønt': ['æble', 'banan', 'tomat', 'agurk', 'salat', 'kartof'],
            'Brød & Bagværk': ['brød', 'bolle', 'kage', 'wienerbrød'],
            'Frost': ['frost', 'frossen', 'is ', 'pizza'],
            'Kolonial': ['pasta', 'ris', 'sauce', 'konserves'],
            'Snacks': ['chips', 'slik', 'chokolade', 'nødder'],
            'Personlig pleje': ['shampoo', 'tandpasta', 'creme', 'deodorant'],
            'Rengøring': ['vaskemiddel', 'opvask', 'rengøring'],
            'Husholdning': ['toiletpapir', 'køkkenrulle', 'folie'],
        }

        scores = {}
        for category, keywords in fallback_keywords.items():
            score = sum(1 for kw in keywords if kw in text)
            if score > 0:
                scores[category] = score

        if scores:
            return max(scores.items(), key=lambda x: x[1])[0]

        return 'Andet'

    def normalize_batch(
        self,
        products: List[str],
        prices: Optional[List[float]] = None,
        batch_size: int = 10
    ) -> List[NormalizedProduct]:
        """
        Normalize multiple products efficiently using batch GPT calls.

        Args:
            products: List of product texts to normalize
            prices: Optional list of prices (same length as products)
            batch_size: Number of products per GPT call (max 10)

        Returns:
            List of NormalizedProduct objects
        """
        results = []
        prices = prices or [None] * len(products)

        # Try batch GPT first
        client = self._get_client()
        if client:
            try:
                return self._normalize_batch_gpt(
                    client, products, prices, min(batch_size, 10)
                )
            except Exception as e:
                logger.warning(f"Batch GPT normalization failed: {e}, falling back to sequential")

        # Fallback to sequential processing
        for product_text, price in zip(products, prices):
            result = self.normalize(product_text, price)
            results.append(result)

        return results

    def _normalize_batch_gpt(
        self,
        client,
        products: List[str],
        prices: List[Optional[float]],
        batch_size: int
    ) -> List[NormalizedProduct]:
        """Normalize products in batches using GPT."""
        all_results = []

        # Process in batches
        for i in range(0, len(products), batch_size):
            batch_products = products[i:i + batch_size]
            batch_prices = prices[i:i + batch_size]

            # Build batch message
            batch_items = []
            for idx, (prod, price) in enumerate(zip(batch_products, batch_prices)):
                item = f"{idx + 1}. {prod}"
                if price:
                    item += f" (pris: {price} kr)"
                batch_items.append(item)

            batch_message = "Normaliser følgende produkter:\n" + "\n".join(batch_items)
            batch_message += "\n\nReturner JSON array med et objekt per produkt i samme rækkefølge."

            try:
                response = client.chat.completions.create(
                    model=self.model_name,
                    messages=[
                        {"role": "system", "content": SYSTEM_PROMPT},
                        {"role": "user", "content": batch_message}
                    ],
                    temperature=0.1,
                    max_tokens=500 * len(batch_products),
                    response_format={"type": "json_object"}
                )

                content = response.choices[0].message.content
                data = json.loads(content)

                # Handle both array and object with "products" key
                if isinstance(data, list):
                    batch_data = data
                elif isinstance(data, dict) and "products" in data:
                    batch_data = data["products"]
                else:
                    # Single result, wrap in list
                    batch_data = [data]

                # Convert to NormalizedProduct objects
                for j, item in enumerate(batch_data):
                    if j < len(batch_products):
                        all_results.append(NormalizedProduct(
                            brand_norm=self._clean_string(item.get("brand_norm")),
                            product_norm=self._clean_string(item.get("product_norm")) or batch_products[j],
                            variant_norm=self._clean_string(item.get("variant_norm")),
                            category=self._validate_category(item.get("category")),
                            net_amount_value=self._to_float(item.get("net_amount_value")),
                            net_amount_unit=self._normalize_unit(item.get("net_amount_unit")),
                            pack_count=self._to_int(item.get("pack_count")),
                            container_type=self._validate_container(item.get("container_type")),
                            deposit_value=self._to_float(item.get("deposit_value")),
                            comment=self._clean_string(item.get("comment")),
                            confidence=0.85  # Slightly lower for batch
                        ))

                # Fill any missing results with fallback
                while len(all_results) < i + len(batch_products):
                    idx = len(all_results) - i
                    if idx < len(batch_products):
                        all_results.append(self._normalize_with_rules(batch_products[idx]))

            except Exception as e:
                logger.warning(f"Batch {i // batch_size} failed: {e}, using rules")
                # Fallback for failed batch
                for prod in batch_products:
                    all_results.append(self._normalize_with_rules(prod))

        return all_results
