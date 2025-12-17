"""
Main Scanner Service
====================

Orchestrates the full scanning pipeline:
1. Document Intelligence (layout extraction)
2. Offer Detection (block grouping)
3. GPT Normalization (product parsing)
4. Unit Price Calculation
5. SKU Key Generation
6. Confidence Scoring
"""

import logging
from dataclasses import dataclass, field
from typing import List, Optional, Tuple
from datetime import datetime

from .document_intelligence import get_layout_service, DocumentLayout
from .offer_detector import OfferDetector, OfferBlock
from .openai_normalizer import OpenAINormalizer, NormalizedProduct
from .unit_price import (
    calculate_unit_price,
    calculate_price_excl_deposit,
    estimate_deposit
)
from .sku_key import generate_sku_key
from .confidence import (
    calculate_confidence,
    ConfidenceInput,
    get_status_from_confidence
)
from .bbox_cropper import BboxCropper, get_cropper

logger = logging.getLogger(__name__)


@dataclass
class ScannedOffer:
    """Final scanned offer ready for API upload."""
    # Raw data
    product_text_raw: str

    # Normalized fields
    brand_norm: Optional[str] = None
    product_norm: Optional[str] = None
    variant_norm: Optional[str] = None
    category: str = "Andet"

    # Amount
    net_amount_value: Optional[float] = None
    net_amount_unit: Optional[str] = None
    pack_count: Optional[int] = None
    container_type: Optional[str] = None

    # Price
    price_value: Optional[float] = None
    deposit_value: Optional[float] = None
    price_excl_deposit: Optional[float] = None

    # Unit price (calculated)
    unit_price_value: Optional[float] = None
    unit_price_unit: Optional[str] = None

    # SKU identity
    sku_key: Optional[str] = None

    # Comment
    comment: Optional[str] = None

    # Confidence
    confidence: float = 0.0
    confidence_details: dict = field(default_factory=dict)
    confidence_reasons: list = field(default_factory=list)

    # Status (based on confidence)
    status: str = "needs_review"  # 'published', 'needs_review', 'low_confidence'

    # Crop URL (for Review UI)
    crop_url: Optional[str] = None

    # Trace
    trace: dict = field(default_factory=dict)


@dataclass
class ScanResult:
    """Result of scanning a PDF."""
    offers: List[ScannedOffer] = field(default_factory=list)

    # Metadata
    retailer: Optional[str] = None
    retailer_confidence: float = 0.0
    valid_from: Optional[str] = None
    valid_to: Optional[str] = None
    validity_confidence: float = 0.0

    # Stats
    total_pages: int = 0
    total_blocks: int = 0
    offers_detected: int = 0
    scanner_version: str = "2.0.0"


class Scanner:
    """
    Main scanner that orchestrates the full pipeline.
    """

    def __init__(
        self,
        openai_api_key: Optional[str] = None,
        openai_endpoint: Optional[str] = None,
        enable_cropping: bool = False
    ):
        """
        Initialize scanner with optional OpenAI credentials.

        Args:
            openai_api_key: Azure OpenAI API key
            openai_endpoint: Azure OpenAI endpoint URL
            enable_cropping: Enable bbox cropping for Review UI
        """
        self.layout_service = get_layout_service()
        self.offer_detector = OfferDetector()
        self.normalizer = OpenAINormalizer(openai_api_key, openai_endpoint)
        self.enable_cropping = enable_cropping
        self.cropper = get_cropper() if enable_cropping else None
        self._pdf_content: Optional[bytes] = None  # Store for cropping

        logger.info(f"Scanner initialized with {type(self.layout_service).__name__}")

    def scan(self, pdf_content: bytes, source_file: Optional[str] = None) -> ScanResult:
        """
        Scan a PDF and extract all offers.

        Args:
            pdf_content: PDF file as bytes
            source_file: Optional source filename for tracing

        Returns:
            ScanResult with all detected and normalized offers
        """
        result = ScanResult()
        self._pdf_content = pdf_content  # Store for cropping

        # Step 1: Extract layout
        logger.info("Step 1: Extracting layout...")
        document = self.layout_service.analyze_pdf(pdf_content)

        result.total_pages = document.total_pages
        result.total_blocks = len(document.all_blocks)

        # Extract retailer and validity from document
        result.retailer = document.retailer_detected
        result.retailer_confidence = document.retailer_confidence

        if document.validity_period:
            result.valid_from, result.valid_to = document.validity_period
            result.validity_confidence = document.validity_confidence

        logger.info(
            f"Layout extracted: {result.total_pages} pages, "
            f"{result.total_blocks} blocks, "
            f"retailer={result.retailer}"
        )

        # Step 2: Detect offer blocks
        logger.info("Step 2: Detecting offer blocks...")
        offer_blocks = self.offer_detector.detect_offers(document)
        result.offers_detected = len(offer_blocks)

        logger.info(f"Detected {len(offer_blocks)} offer candidates")

        # Step 3: Normalize each offer
        logger.info("Step 3: Normalizing offers...")
        for block in offer_blocks:
            try:
                offer = self._process_offer_block(block, source_file, result.retailer)
                if offer:
                    result.offers.append(offer)
            except Exception as e:
                logger.warning(f"Failed to process offer block: {e}")

        logger.info(f"Scan complete: {len(result.offers)} offers extracted")

        return result

    def _process_offer_block(
        self,
        block: OfferBlock,
        source_file: Optional[str],
        retailer: Optional[str] = None
    ) -> Optional[ScannedOffer]:
        """Process a single offer block into a ScannedOffer."""

        if not block.price:
            return None

        # Normalize with GPT
        normalized = self.normalizer.normalize(
            block.product_text,
            block.price.value if block.price else None
        )

        # Extract quantity from block if normalizer didn't find it
        net_amount_value = normalized.net_amount_value
        net_amount_unit = normalized.net_amount_unit
        pack_count = normalized.pack_count

        if not net_amount_value and block.quantity_text:
            qty = self._parse_quantity(block.quantity_text)
            if qty:
                net_amount_value, net_amount_unit, pack_count = qty

        # Handle deposit - use estimate if not from normalizer
        deposit_value = normalized.deposit_value
        if deposit_value is None and block.has_deposit:
            deposit_value = estimate_deposit(
                normalized.container_type,
                net_amount_value,
                net_amount_unit,
                pack_count
            )

        # Calculate price excluding deposit
        price_value = block.price.value if block.price else None
        price_excl_deposit = calculate_price_excl_deposit(price_value, deposit_value)

        # Calculate unit price (deterministic)
        unit_price = calculate_unit_price(
            price_value,
            deposit_value,
            net_amount_value,
            net_amount_unit,
            pack_count
        )

        unit_price_value = unit_price.value if unit_price else None
        unit_price_unit = unit_price.unit if unit_price else None

        # Generate SKU key (deterministic)
        sku_key = generate_sku_key(
            normalized.brand_norm,
            normalized.product_norm or block.product_text,
            normalized.variant_norm,
            normalized.container_type,
            net_amount_value,
            net_amount_unit
        )

        # Calculate confidence using dedicated module
        confidence_input = ConfidenceInput(
            detection_confidence=block.detection_confidence,
            has_price=block.price is not None,
            price_value=price_value,
            has_amount=net_amount_value is not None,
            net_amount_value=net_amount_value,
            net_amount_unit=net_amount_unit,
            gpt_confidence=normalized.confidence,
            brand_norm=normalized.brand_norm,
            product_norm=normalized.product_norm,
            category=normalized.category,
            container_type=normalized.container_type,
            has_unit_price=unit_price is not None
        )
        confidence_result = calculate_confidence(confidence_input)

        overall_confidence = confidence_result.overall
        confidence_details = confidence_result.details
        confidence_reasons = confidence_result.reasons
        status = get_status_from_confidence(overall_confidence)

        # Build trace
        trace = {
            "page": block.page,
            "bbox": list(block.bbox),
            "text_lines": block.text_lines,
            "source_file": source_file,
        }

        # Combine comments
        comment = normalized.comment or block.comment_text

        # Crop bbox for Review UI
        crop_url = None
        if self.enable_cropping and self.cropper and self._pdf_content:
            offer_id = self.cropper.generate_offer_id(
                retailer=retailer or "unknown",
                page=block.page,
                bbox=block.bbox,
                product_text=block.product_text
            )
            crop_result = self.cropper.crop_and_upload(
                pdf_content=self._pdf_content,
                page=block.page,
                bbox=block.bbox,
                offer_id=offer_id
            )
            if crop_result.success and crop_result.blob_url:
                crop_url = crop_result.blob_url

        return ScannedOffer(
            product_text_raw=block.product_text,
            brand_norm=normalized.brand_norm,
            product_norm=normalized.product_norm or block.product_text,
            variant_norm=normalized.variant_norm,
            category=normalized.category,
            net_amount_value=net_amount_value,
            net_amount_unit=net_amount_unit,
            pack_count=pack_count,
            container_type=normalized.container_type,
            price_value=price_value,
            deposit_value=deposit_value,
            price_excl_deposit=price_excl_deposit,
            unit_price_value=unit_price_value,
            unit_price_unit=unit_price_unit,
            sku_key=sku_key,
            comment=comment,
            confidence=overall_confidence,
            confidence_details=confidence_details,
            confidence_reasons=confidence_reasons,
            status=status,
            crop_url=crop_url,
            trace=trace
        )

    def _parse_quantity(self, text: str) -> Optional[Tuple[float, str, Optional[int]]]:
        """Parse quantity text into (value, unit, pack_count)."""
        import re

        text = text.lower().strip()

        # Pattern: "6 x 33 cl" -> pack=6, value=33, unit=cl
        multi_match = re.search(r'(\d+)\s*x\s*(\d+)\s*(g|ml|cl|dl|l|stk)', text)
        if multi_match:
            pack = int(multi_match.group(1))
            value = float(multi_match.group(2))
            unit = multi_match.group(3)
            return (value, unit, pack)

        # Pattern: "6-pak" -> pack=6
        pak_match = re.search(r'(\d+)\s*-?\s*pak', text)
        if pak_match:
            pack = int(pak_match.group(1))
            return (None, None, pack)

        # Pattern: "500 g" or "33 cl"
        simple_match = re.search(r'(\d+(?:[.,]\d+)?)\s*(g|kg|ml|cl|dl|l|liter|stk)', text)
        if simple_match:
            value = float(simple_match.group(1).replace(',', '.'))
            unit = simple_match.group(2)
            return (value, unit, None)

        return None


def scan_pdf(pdf_content: bytes, source_file: Optional[str] = None) -> ScanResult:
    """
    Convenience function to scan a PDF.

    Args:
        pdf_content: PDF file as bytes
        source_file: Optional source filename

    Returns:
        ScanResult with all offers
    """
    scanner = Scanner()
    return scanner.scan(pdf_content, source_file)
