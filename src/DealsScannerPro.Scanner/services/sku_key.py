"""
SKU Key Generator
=================

Generates deterministic SKU keys from normalized offer data.
Matches the C# implementation in SkuKeyGenerator.cs.

SKU Key Format:
{brand_norm}|{product_norm}|{variant_norm}|{container_type}|{net_amount_value}{net_amount_unit}

Rules:
- Danish characters normalized: æ→ae, ø→oe, å→aa
- All lowercase
- Special characters removed (except hyphen)
- Spaces replaced with hyphens
- pack_count NOT included (allows comparison across pack sizes)
- Amount normalized to base units (ml, g)
"""

import re
from typing import Optional


def generate_sku_key(
    brand_norm: Optional[str],
    product_norm: Optional[str],
    variant_norm: Optional[str],
    container_type: Optional[str],
    net_amount_value: Optional[float],
    net_amount_unit: Optional[str]
) -> Optional[str]:
    """
    Generate a deterministic SKU key from offer fields.

    Args:
        brand_norm: Normalized brand name
        product_norm: Normalized product name (required)
        variant_norm: Normalized variant
        container_type: Container type (CAN, BOTTLE, etc.)
        net_amount_value: Amount value
        net_amount_unit: Amount unit (ml, g, l, kg, etc.)

    Returns:
        SKU key string, or None if product_norm is missing
    """
    # Product norm is required
    if not product_norm or not product_norm.strip():
        return None

    parts = [
        normalize_text(brand_norm) or "null",
        normalize_text(product_norm),
        normalize_text(variant_norm) or "null",
        normalize_text(container_type) or "null",
        format_amount(net_amount_value, net_amount_unit)
    ]

    return "|".join(parts)


def normalize_text(text: Optional[str]) -> Optional[str]:
    """
    Normalize text for use in SKU key.

    - Lowercase
    - Replace Danish characters (æ→ae, ø→oe, å→aa)
    - Remove special characters except hyphen
    - Replace spaces with hyphens
    - Remove consecutive hyphens
    """
    if not text:
        return None

    result = text.lower().strip()

    # Replace Danish characters
    replacements = {
        'æ': 'ae',
        'ø': 'oe',
        'å': 'aa',
        'Æ': 'ae',
        'Ø': 'oe',
        'Å': 'aa',
    }
    for danish, replacement in replacements.items():
        result = result.replace(danish, replacement)

    # Remove special characters except hyphen and space
    result = re.sub(r'[^a-z0-9\-\s]', '', result)

    # Replace spaces with hyphen
    result = re.sub(r'\s+', '-', result)

    # Remove consecutive hyphens
    result = re.sub(r'-+', '-', result)

    # Trim hyphens from start/end
    result = result.strip('-')

    return result if result else None


def format_amount(value: Optional[float], unit: Optional[str]) -> str:
    """
    Format amount as "{value}{unit}" for SKU key.
    Normalizes units: L→ml (x1000), kg→g (x1000)

    Args:
        value: Amount value
        unit: Amount unit

    Returns:
        Formatted string like "500g" or "330ml", or "null" if missing
    """
    if value is None or unit is None:
        return "null"

    normalized_unit = unit.lower().strip()
    normalized_value = value

    # Normalize to base units (ml for volume, g for weight)
    unit_conversions = {
        'l': ('ml', 1000),
        'liter': ('ml', 1000),
        'dl': ('ml', 100),
        'cl': ('ml', 10),
        'kg': ('g', 1000),
        'kilo': ('g', 1000),
        'kilogram': ('g', 1000),
    }

    if normalized_unit in unit_conversions:
        new_unit, multiplier = unit_conversions[normalized_unit]
        normalized_value = value * multiplier
        normalized_unit = new_unit

    # Round to avoid floating point issues
    normalized_value = round(normalized_value)

    return f"{int(normalized_value)}{normalized_unit}"


def parse_sku_key(sku_key: str) -> dict:
    """
    Parse a SKU key back into its components.

    Args:
        sku_key: SKU key string

    Returns:
        Dictionary with brand, product, variant, container, amount
    """
    parts = sku_key.split("|")

    if len(parts) != 5:
        return {}

    result = {
        "brand": parts[0] if parts[0] != "null" else None,
        "product": parts[1] if parts[1] != "null" else None,
        "variant": parts[2] if parts[2] != "null" else None,
        "container": parts[3] if parts[3] != "null" else None,
        "amount": parts[4] if parts[4] != "null" else None,
    }

    # Parse amount into value and unit
    if result["amount"]:
        match = re.match(r'(\d+)(\w+)', result["amount"])
        if match:
            result["amount_value"] = int(match.group(1))
            result["amount_unit"] = match.group(2)

    return result


def sku_keys_match(key1: Optional[str], key2: Optional[str]) -> bool:
    """
    Check if two SKU keys represent the same product.

    Args:
        key1: First SKU key
        key2: Second SKU key

    Returns:
        True if keys match
    """
    if not key1 or not key2:
        return False

    return key1.lower() == key2.lower()


# Example usage and tests
if __name__ == "__main__":
    # Test cases
    test_cases = [
        {
            "brand": "Coca-Cola",
            "product": "Cola",
            "variant": "Original",
            "container": "CAN",
            "amount": 330,
            "unit": "ml",
            "expected": "coca-cola|cola|original|can|330ml"
        },
        {
            "brand": "Arla",
            "product": "Letmælk",
            "variant": "Økologisk",
            "container": "BOTTLE",
            "amount": 1,
            "unit": "L",
            "expected": "arla|letmaelk|oekologisk|bottle|1000ml"
        },
        {
            "brand": None,
            "product": "Hakket oksekød",
            "variant": "8-12% fedt",
            "container": "TRAY",
            "amount": 500,
            "unit": "g",
            "expected": "null|hakket-oksekoed|8-12-fedt|tray|500g"
        },
    ]

    print("SKU Key Generator Tests:")
    print("=" * 60)

    for i, tc in enumerate(test_cases):
        result = generate_sku_key(
            tc["brand"],
            tc["product"],
            tc["variant"],
            tc["container"],
            tc["amount"],
            tc["unit"]
        )
        status = "✓" if result == tc["expected"] else "✗"
        print(f"\nTest {i + 1}: {status}")
        print(f"  Input: {tc['brand']} / {tc['product']} / {tc['variant']}")
        print(f"  Expected: {tc['expected']}")
        print(f"  Got:      {result}")
