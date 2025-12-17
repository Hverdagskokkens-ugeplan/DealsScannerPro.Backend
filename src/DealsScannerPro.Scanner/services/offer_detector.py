"""
Offer Block Detector
====================

Detects offer blocks from Document Intelligence layout output.
Groups related text blocks (product name, price, quantity, etc.)
into candidate offers for further processing.
"""

import re
import logging
from dataclasses import dataclass, field
from typing import List, Optional, Tuple
from .document_intelligence import DocumentLayout, PageLayout, TextBlock

logger = logging.getLogger(__name__)


@dataclass
class PriceInfo:
    """Extracted price information."""
    value: float
    original_text: str
    is_unit_price: bool = False  # True if kr/kg, kr/L, etc.
    unit: Optional[str] = None  # kg, L, stk for unit prices
    has_deposit_mention: bool = False


@dataclass
class OfferBlock:
    """A detected offer block with all associated text."""
    # Position
    page: int
    bbox: Tuple[float, float, float, float]  # Combined bbox of all blocks

    # Raw text
    text_blocks: List[TextBlock] = field(default_factory=list)
    combined_text: str = ""

    # Extracted components
    product_text: str = ""
    price: Optional[PriceInfo] = None
    unit_price: Optional[PriceInfo] = None
    quantity_text: Optional[str] = None
    has_deposit: bool = False
    comment_text: Optional[str] = None

    # Detection confidence
    detection_confidence: float = 0.0

    @property
    def text_lines(self) -> List[str]:
        """Get individual text lines."""
        return [b.text for b in self.text_blocks]


class OfferDetector:
    """
    Detects offer blocks from document layout.

    Strategy:
    1. Find all price patterns in text blocks
    2. For each price, find nearby product text
    3. Group blocks spatially into offer candidates
    4. Filter out non-offer content
    """

    # Price patterns
    PRICE_PATTERNS = [
        # "25.-" or "25,-"
        (r'(\d+)[.,]-', lambda m: float(m.group(1)), False),
        # "25,95" or "25.95"
        (r'(\d+)[,.](\d{2})(?!\d)', lambda m: float(f"{m.group(1)}.{m.group(2)}"), False),
        # "25 kr" or "25kr"
        (r'(\d+)\s*kr\.?(?!\s*/)', lambda m: float(m.group(1)), False),
        # "25,95 kr"
        (r'(\d+)[,.](\d{2})\s*kr\.?(?!\s*/)', lambda m: float(f"{m.group(1)}.{m.group(2)}"), False),
        # Unit prices: "49,95/kg" or "49,95 kr/kg"
        (r'(\d+)[,.](\d{2})\s*(?:kr\.?)?\s*/\s*(kg|l|liter|stk)',
         lambda m: float(f"{m.group(1)}.{m.group(2)}"), True),
        # "49/kg" or "49 kr/kg"
        (r'(\d+)\s*(?:kr\.?)?\s*/\s*(kg|l|liter|stk)',
         lambda m: float(m.group(1)), True),
    ]

    # Skip patterns - text that indicates non-offer content
    SKIP_PATTERNS = [
        r'^side\s*\d+',  # Page numbers
        r'^\d+\s*$',  # Just numbers
        r'^uge\s*\d+',  # Week numbers
        r'^gyldig',  # Validity info
        r'^tilbuddene\s+gælder',
        r'^max\s*\d+\s*(stk|kg|pr)',  # Max purchase limits (extract as comment)
        r'^vi\s+tager\s+forbehold',
        r'^se\s+flere\s+tilbud',
        r'^download\s+app',
        r'^scan\s+(koden|og)',
        r'^følg\s+os',
        r'^find\s+din\s+nærmeste',
        r'^\*+$',  # Just asterisks
        r'^www\.',
        r'\.dk$',
        r'facebook|instagram',
        r'^coop\s*medlem',
        r'^kun\s+med\s+\w+kort',  # Loyalty card requirements (extract as comment)
    ]

    # Patterns that indicate quantity/amount
    QUANTITY_PATTERNS = [
        r'(\d+)\s*(g|gram|kg|kilo|ml|cl|dl|l|liter|stk|pk|pak)\b',
        r'(\d+)\s*x\s*(\d+)\s*(g|ml|cl|l|stk)',  # 6 x 33 cl
        r'(\d+)-pak',  # 6-pak
        r'(\d+)\s*stk',
    ]

    # Comment patterns (restrictions, special conditions)
    COMMENT_PATTERNS = [
        r'max\.?\s*(\d+)\s*(stk|kg|pr\.?\s*kunde)',
        r'kun\s+(med\s+)?\w+[-\s]?kort',
        r'medlemspris',
        r'spar\s+\d+%?',
        r'før\s+\d+[,.]?\d*',
        r'normalpris\s+\d+[,.]?\d*',
    ]

    def __init__(self,
                 max_block_distance: float = 0.15,
                 min_price: float = 1.0,
                 max_price: float = 9999.0):
        """
        Initialize detector.

        Args:
            max_block_distance: Max normalized distance between blocks to group (0-1)
            min_price: Minimum valid price
            max_price: Maximum valid price
        """
        self.max_block_distance = max_block_distance
        self.min_price = min_price
        self.max_price = max_price

    def detect_offers(self, document: DocumentLayout) -> List[OfferBlock]:
        """
        Detect all offer blocks in a document.

        Args:
            document: DocumentLayout from Document Intelligence

        Returns:
            List of detected OfferBlock candidates
        """
        all_offers = []

        for page in document.pages:
            page_offers = self._detect_offers_on_page(page)
            all_offers.extend(page_offers)

        logger.info(f"Detected {len(all_offers)} offer candidates")
        return all_offers

    def _detect_offers_on_page(self, page: PageLayout) -> List[OfferBlock]:
        """Detect offers on a single page."""
        offers = []

        # First, find all blocks with prices
        price_blocks = []
        for block in page.blocks:
            price_info = self._extract_price(block.text)
            if price_info and not price_info.is_unit_price:
                price_blocks.append((block, price_info))

        logger.debug(f"Page {page.page_number}: Found {len(price_blocks)} price blocks")

        # For each price block, find associated product text
        used_blocks = set()

        for price_block, price_info in price_blocks:
            if id(price_block) in used_blocks:
                continue

            # Find nearby blocks that could be product text
            nearby_blocks = self._find_nearby_blocks(
                price_block, page.blocks, used_blocks
            )

            if not nearby_blocks:
                continue

            # Create offer block
            all_blocks = nearby_blocks + [price_block]
            offer = self._create_offer_block(
                all_blocks, price_block, price_info, page.page_number
            )

            if offer and offer.detection_confidence > 0.3:
                offers.append(offer)
                # Mark blocks as used
                for b in all_blocks:
                    used_blocks.add(id(b))

        return offers

    def _extract_price(self, text: str) -> Optional[PriceInfo]:
        """Extract price from text."""
        text_lower = text.lower().strip()

        # Check if should skip
        for pattern in self.SKIP_PATTERNS:
            if re.search(pattern, text_lower, re.IGNORECASE):
                return None

        # Try each price pattern
        for pattern, extractor, is_unit_price in self.PRICE_PATTERNS:
            match = re.search(pattern, text_lower, re.IGNORECASE)
            if match:
                try:
                    value = extractor(match)
                    if self.min_price <= value <= self.max_price:
                        unit = None
                        if is_unit_price and len(match.groups()) >= 3:
                            unit = match.group(len(match.groups()))

                        return PriceInfo(
                            value=value,
                            original_text=text,
                            is_unit_price=is_unit_price,
                            unit=unit,
                            has_deposit_mention='pant' in text_lower
                        )
                except (ValueError, IndexError):
                    continue

        return None

    def _find_nearby_blocks(
        self,
        target: TextBlock,
        all_blocks: List[TextBlock],
        used_blocks: set
    ) -> List[TextBlock]:
        """Find text blocks near the target (likely product text)."""
        nearby = []

        for block in all_blocks:
            if id(block) in used_blocks:
                continue
            if block is target:
                continue

            # Calculate distance
            distance = self._block_distance(target, block)

            if distance <= self.max_block_distance:
                # Check if it's valid product text
                if self._is_valid_product_text(block.text):
                    nearby.append(block)

        # Sort by vertical position (top to bottom)
        nearby.sort(key=lambda b: b.y1)

        return nearby

    def _block_distance(self, a: TextBlock, b: TextBlock) -> float:
        """Calculate normalized distance between two blocks."""
        # Use center-to-center distance
        dx = abs(a.center_x - b.center_x)
        dy = abs(a.center_y - b.center_y)

        # Weight vertical distance more (offers are usually stacked)
        return (dx * 0.5) + dy

    def _is_valid_product_text(self, text: str) -> bool:
        """Check if text could be product text."""
        text = text.strip()

        # Too short
        if len(text) < 2:
            return False

        # Just numbers
        if re.match(r'^[\d.,\s]+$', text):
            return False

        # Skip patterns
        text_lower = text.lower()
        for pattern in self.SKIP_PATTERNS:
            if re.search(pattern, text_lower, re.IGNORECASE):
                return False

        # Has some letters
        if not re.search(r'[a-zæøåA-ZÆØÅ]', text):
            return False

        return True

    def _create_offer_block(
        self,
        blocks: List[TextBlock],
        price_block: TextBlock,
        price_info: PriceInfo,
        page: int
    ) -> Optional[OfferBlock]:
        """Create an OfferBlock from grouped blocks."""

        if not blocks:
            return None

        # Calculate combined bbox
        x1 = min(b.x1 for b in blocks)
        y1 = min(b.y1 for b in blocks)
        x2 = max(b.x2 for b in blocks)
        y2 = max(b.y2 for b in blocks)

        # Sort blocks by vertical position
        sorted_blocks = sorted(blocks, key=lambda b: (b.y1, b.x1))

        # Extract components
        product_parts = []
        quantity_text = None
        unit_price = None
        comment_text = None

        for block in sorted_blocks:
            text = block.text.strip()

            if block is price_block:
                continue

            # Check for unit price
            up = self._extract_price(text)
            if up and up.is_unit_price:
                unit_price = up
                continue

            # Check for quantity
            qty_match = None
            for pattern in self.QUANTITY_PATTERNS:
                qty_match = re.search(pattern, text, re.IGNORECASE)
                if qty_match:
                    quantity_text = qty_match.group(0)
                    break

            # Check for comments
            for pattern in self.COMMENT_PATTERNS:
                comment_match = re.search(pattern, text, re.IGNORECASE)
                if comment_match:
                    comment_text = comment_match.group(0)
                    # Don't add to product text
                    continue

            # Otherwise it's product text
            if self._is_valid_product_text(text):
                # Remove quantity from product text if found
                clean_text = text
                if qty_match:
                    clean_text = text[:qty_match.start()] + text[qty_match.end():]
                    clean_text = clean_text.strip()

                if clean_text:
                    product_parts.append(clean_text)

        product_text = " ".join(product_parts).strip()

        # Must have product text
        if not product_text:
            return None

        # Calculate confidence
        confidence = self._calculate_detection_confidence(
            product_text, price_info, quantity_text, unit_price
        )

        return OfferBlock(
            page=page,
            bbox=(x1, y1, x2, y2),
            text_blocks=sorted_blocks,
            combined_text=" ".join(b.text for b in sorted_blocks),
            product_text=product_text,
            price=price_info,
            unit_price=unit_price,
            quantity_text=quantity_text,
            has_deposit=price_info.has_deposit_mention or 'pant' in product_text.lower(),
            comment_text=comment_text,
            detection_confidence=confidence
        )

    def _calculate_detection_confidence(
        self,
        product_text: str,
        price: PriceInfo,
        quantity: Optional[str],
        unit_price: Optional[PriceInfo]
    ) -> float:
        """Calculate confidence that this is a valid offer."""
        confidence = 0.5  # Base confidence

        # Has meaningful product text
        if len(product_text) > 5:
            confidence += 0.1
        if len(product_text) > 15:
            confidence += 0.1

        # Has reasonable price
        if 5 <= price.value <= 500:
            confidence += 0.1

        # Has quantity
        if quantity:
            confidence += 0.1

        # Has unit price (very good signal)
        if unit_price:
            confidence += 0.15

        # Product text looks like a product (has brand-like words)
        if re.search(r'[A-ZÆØÅ][a-zæøå]+', product_text):  # Capitalized word
            confidence += 0.05

        # Penalize very short product text
        if len(product_text) < 4:
            confidence -= 0.2

        # Penalize if product text is mostly numbers
        letters = len(re.findall(r'[a-zæøåA-ZÆØÅ]', product_text))
        if letters < len(product_text) * 0.5:
            confidence -= 0.1

        return min(1.0, max(0.0, confidence))
