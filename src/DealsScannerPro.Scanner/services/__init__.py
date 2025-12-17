# Services package
from .document_intelligence import (
    DocumentIntelligenceService,
    PyMuPDFLayoutService,
    DocumentLayout,
    PageLayout,
    TextBlock,
    get_layout_service
)
from .offer_detector import OfferDetector, OfferBlock, PriceInfo
from .openai_normalizer import OpenAINormalizer, NormalizedProduct
from .unit_price import (
    UnitPrice,
    calculate_unit_price,
    calculate_price_excl_deposit,
    estimate_deposit,
    normalize_amount_to_base_unit
)
from .sku_key import (
    generate_sku_key,
    normalize_text,
    parse_sku_key,
    sku_keys_match
)
from .scanner import Scanner, ScannedOffer, ScanResult, scan_pdf
from .confidence import (
    ConfidenceInput,
    ConfidenceResult,
    calculate_confidence,
    should_auto_publish,
    get_status_from_confidence
)
from .bbox_cropper import (
    BboxCropper,
    CropResult,
    get_cropper
)

__all__ = [
    # Document Intelligence
    'DocumentIntelligenceService',
    'PyMuPDFLayoutService',
    'DocumentLayout',
    'PageLayout',
    'TextBlock',
    'get_layout_service',
    # Offer Detection
    'OfferDetector',
    'OfferBlock',
    'PriceInfo',
    # OpenAI Normalization
    'OpenAINormalizer',
    'NormalizedProduct',
    # Unit Price
    'UnitPrice',
    'calculate_unit_price',
    'calculate_price_excl_deposit',
    'estimate_deposit',
    'normalize_amount_to_base_unit',
    # SKU Key
    'generate_sku_key',
    'normalize_text',
    'parse_sku_key',
    'sku_keys_match',
    # Main Scanner
    'Scanner',
    'ScannedOffer',
    'ScanResult',
    'scan_pdf',
    # Confidence Scoring
    'ConfidenceInput',
    'ConfidenceResult',
    'calculate_confidence',
    'should_auto_publish',
    'get_status_from_confidence',
    # Bbox Cropper
    'BboxCropper',
    'CropResult',
    'get_cropper',
]
