"""
Confidence Scoring
==================

Calculates confidence scores for scanned offers.
Used to determine if offers can be auto-published (>=0.9)
or require human review (<0.9).

Scoring Factors:
- Detection confidence (from block detection)
- Price extraction (did we find a valid price?)
- Amount extraction (did we find quantity?)
- GPT normalization confidence
- Data completeness

Weights (total = 1.0):
- Price: 0.35 (most critical)
- Detection: 0.25 (block grouping quality)
- GPT normalization: 0.20 (brand/product accuracy)
- Amount: 0.15 (quantity found)
- Completeness: 0.05 (all fields present)
"""

from dataclasses import dataclass, field
from typing import Optional


@dataclass
class ConfidenceResult:
    """Result of confidence calculation."""
    overall: float
    details: dict = field(default_factory=dict)
    can_auto_publish: bool = False
    reasons: list = field(default_factory=list)

    def __post_init__(self):
        self.can_auto_publish = self.overall >= 0.9


@dataclass
class ConfidenceInput:
    """Input data for confidence calculation."""
    # Detection
    detection_confidence: float = 0.0

    # Price
    has_price: bool = False
    price_value: Optional[float] = None

    # Amount
    has_amount: bool = False
    net_amount_value: Optional[float] = None
    net_amount_unit: Optional[str] = None

    # GPT normalization
    gpt_confidence: float = 0.0
    brand_norm: Optional[str] = None
    product_norm: Optional[str] = None
    category: Optional[str] = None

    # Container
    container_type: Optional[str] = None

    # Unit price (calculated successfully?)
    has_unit_price: bool = False


# Weights for each factor
WEIGHTS = {
    'price': 0.35,
    'detection': 0.25,
    'gpt': 0.20,
    'amount': 0.15,
    'completeness': 0.05
}


def calculate_confidence(input: ConfidenceInput) -> ConfidenceResult:
    """
    Calculate overall confidence score from input signals.

    Args:
        input: ConfidenceInput with all signals

    Returns:
        ConfidenceResult with overall score, details, and reasons
    """
    details = {}
    reasons = []

    # 1. Price score (0.0 - 1.0)
    if input.has_price and input.price_value and input.price_value > 0:
        # Valid price found
        if input.price_value < 1:
            price_score = 0.7  # Suspiciously low price
            reasons.append("Mistænkelig lav pris (<1 kr)")
        elif input.price_value > 5000:
            price_score = 0.6  # Suspiciously high price
            reasons.append("Mistænkelig høj pris (>5000 kr)")
        else:
            price_score = 1.0
    else:
        price_score = 0.0
        reasons.append("Ingen pris fundet")

    details['price'] = price_score

    # 2. Detection score (from block detector)
    detection_score = min(1.0, max(0.0, input.detection_confidence))
    details['detection'] = detection_score

    if detection_score < 0.5:
        reasons.append("Lav blok-detektions confidence")

    # 3. GPT normalization score
    gpt_score = min(1.0, max(0.0, input.gpt_confidence))

    # Boost if we got good normalization
    if input.product_norm and len(input.product_norm) >= 3:
        gpt_score = max(gpt_score, 0.6)

    if input.brand_norm:
        gpt_score = min(1.0, gpt_score + 0.1)  # Bonus for brand

    if input.category and input.category != "Andet":
        gpt_score = min(1.0, gpt_score + 0.05)  # Bonus for specific category

    details['gpt'] = gpt_score

    if gpt_score < 0.5:
        reasons.append("Lav GPT-normaliserings confidence")

    # 4. Amount score
    if input.has_amount and input.net_amount_value and input.net_amount_unit:
        # Check for reasonable values
        if input.net_amount_value <= 0:
            amount_score = 0.3
            reasons.append("Ugyldig mængde-værdi")
        elif input.net_amount_unit.lower() not in ('g', 'kg', 'ml', 'cl', 'dl', 'l', 'liter', 'stk', 'stk.', 'pk', 'pak'):
            amount_score = 0.7
            reasons.append(f"Ukendt mængde-enhed: {input.net_amount_unit}")
        else:
            amount_score = 1.0
    else:
        amount_score = 0.5  # Missing amount is common, not critical
        reasons.append("Ingen mængde fundet")

    details['amount'] = amount_score

    # 5. Completeness score
    completeness_fields = [
        input.has_price,
        input.product_norm is not None,
        input.has_amount,
        input.container_type is not None,
        input.has_unit_price,
    ]
    completeness_score = sum(1 for f in completeness_fields if f) / len(completeness_fields)
    details['completeness'] = completeness_score

    # Calculate weighted overall score
    overall = (
        details['price'] * WEIGHTS['price'] +
        details['detection'] * WEIGHTS['detection'] +
        details['gpt'] * WEIGHTS['gpt'] +
        details['amount'] * WEIGHTS['amount'] +
        details['completeness'] * WEIGHTS['completeness']
    )

    # Round to 2 decimals
    overall = round(overall, 2)

    # Additional penalties for critical issues
    if not input.has_price:
        overall = min(overall, 0.3)  # Cap at 0.3 without price

    if not input.product_norm:
        overall = min(overall, 0.5)  # Cap at 0.5 without product name

    result = ConfidenceResult(
        overall=overall,
        details=details,
        reasons=reasons if reasons else ["Alle felter OK"]
    )

    return result


def should_auto_publish(confidence: float) -> bool:
    """
    Determine if an offer should be auto-published.

    Args:
        confidence: Overall confidence score (0.0 - 1.0)

    Returns:
        True if confidence >= 0.9
    """
    return confidence >= 0.9


def get_status_from_confidence(confidence: float) -> str:
    """
    Get offer status based on confidence.

    Args:
        confidence: Overall confidence score

    Returns:
        Status string: 'published', 'needs_review', or 'low_confidence'
    """
    if confidence >= 0.9:
        return 'published'
    elif confidence >= 0.5:
        return 'needs_review'
    else:
        return 'low_confidence'


# Example usage and tests
if __name__ == "__main__":
    print("Confidence Scoring Tests")
    print("=" * 60)

    # Test 1: High confidence offer
    input1 = ConfidenceInput(
        detection_confidence=0.95,
        has_price=True,
        price_value=29.95,
        has_amount=True,
        net_amount_value=500,
        net_amount_unit='g',
        gpt_confidence=0.9,
        brand_norm="Arla",
        product_norm="Letmælk",
        category="Mejeri",
        container_type="BOTTLE",
        has_unit_price=True
    )
    result1 = calculate_confidence(input1)
    print(f"\nTest 1: Høj confidence tilbud")
    print(f"  Overall: {result1.overall}")
    print(f"  Details: {result1.details}")
    print(f"  Auto-publish: {result1.can_auto_publish}")
    print(f"  Status: {get_status_from_confidence(result1.overall)}")

    # Test 2: Missing price
    input2 = ConfidenceInput(
        detection_confidence=0.8,
        has_price=False,
        has_amount=True,
        net_amount_value=330,
        net_amount_unit='ml',
        gpt_confidence=0.85,
        product_norm="Cola",
        category="Drikkevarer"
    )
    result2 = calculate_confidence(input2)
    print(f"\nTest 2: Manglende pris")
    print(f"  Overall: {result2.overall}")
    print(f"  Auto-publish: {result2.can_auto_publish}")
    print(f"  Reasons: {result2.reasons}")
    print(f"  Status: {get_status_from_confidence(result2.overall)}")

    # Test 3: Low detection confidence
    input3 = ConfidenceInput(
        detection_confidence=0.4,
        has_price=True,
        price_value=15.00,
        has_amount=False,
        gpt_confidence=0.6,
        product_norm="Rugbrød"
    )
    result3 = calculate_confidence(input3)
    print(f"\nTest 3: Lav detektions-confidence")
    print(f"  Overall: {result3.overall}")
    print(f"  Auto-publish: {result3.can_auto_publish}")
    print(f"  Reasons: {result3.reasons}")
    print(f"  Status: {get_status_from_confidence(result3.overall)}")

    # Test 4: Suspicious price
    input4 = ConfidenceInput(
        detection_confidence=0.9,
        has_price=True,
        price_value=0.50,  # Too low
        has_amount=True,
        net_amount_value=1,
        net_amount_unit='kg',
        gpt_confidence=0.8,
        product_norm="Oksekød"
    )
    result4 = calculate_confidence(input4)
    print(f"\nTest 4: Mistænkelig pris")
    print(f"  Overall: {result4.overall}")
    print(f"  Auto-publish: {result4.can_auto_publish}")
    print(f"  Reasons: {result4.reasons}")
