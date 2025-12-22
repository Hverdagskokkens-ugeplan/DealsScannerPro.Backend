"""
DealsScannerPro Scanners
========================

Scanner implementations for Danish supermarket flyers.
"""

import logging
from .netto_scanner import ScannerV2 as NettoScanner
from .rema_scanner import RemaScanner

__all__ = ['NettoScanner', 'RemaScanner', 'detect_store', 'get_scanner', 'SUPPORTED_STORES']

logger = logging.getLogger(__name__)

# Supported stores with their detection patterns
STORE_PATTERNS = {
    # Salling Group stores (similar format)
    'netto': {
        'keywords': ['netto', 'døgnnet', 'nettodag', 'netto-priser'],
        'exclusive': ['netto'],  # Must have these to be certain
        'scanner': 'netto'
    },
    'foetex': {
        'keywords': ['føtex', 'foetex', 'fotex', 'føtex tilbud'],
        'exclusive': ['føtex', 'foetex'],
        'scanner': 'netto'  # Use Netto scanner (same Salling Group format)
    },
    'bilka': {
        'keywords': ['bilka', 'bilka tilbud', 'bilkatilbud'],
        'exclusive': ['bilka'],
        'scanner': 'netto'  # Use Netto scanner (same Salling Group format)
    },
    # Coop stores
    'superbrugsen': {
        'keywords': ['superbrugsen', 'super brugsen', 'brugsen', 'dagli brugsen'],
        'exclusive': ['superbrugsen', 'super brugsen'],
        'scanner': 'netto'  # Generic scanner for now
    },
    'kvickly': {
        'keywords': ['kvickly', 'kvickly tilbud'],
        'exclusive': ['kvickly'],
        'scanner': 'netto'
    },
    '365discount': {
        'keywords': ['365discount', '365 discount', 'coop 365', '365'],
        'exclusive': ['365discount', 'coop 365'],
        'scanner': 'netto'
    },
    # Independent stores
    'rema': {
        'keywords': ['rema', 'rema1000', 'rema 1000', 'meget mere', 'altid billig'],
        'exclusive': ['rema', 'rema1000', 'rema 1000'],
        'scanner': 'rema'
    },
    'lidl': {
        'keywords': ['lidl', 'lidl danmark'],
        'exclusive': ['lidl'],
        'scanner': 'netto'  # Generic scanner for now
    },
    'spar': {
        'keywords': ['spar', 'eurospar', 'spar danmark'],
        'exclusive': ['spar', 'eurospar'],
        'scanner': 'netto'
    },
    'aldi': {
        'keywords': ['aldi', 'aldi nord', 'aldi danmark'],
        'exclusive': ['aldi'],
        'scanner': 'netto'
    },
    'meny': {
        'keywords': ['meny', 'meny tilbud'],
        'exclusive': ['meny'],
        'scanner': 'netto'
    },
    'irma': {
        'keywords': ['irma', 'irma tilbud'],
        'exclusive': ['irma'],
        'scanner': 'netto'
    }
}

# List of all supported stores
SUPPORTED_STORES = list(STORE_PATTERNS.keys())


def detect_store(pdf_content: bytes) -> str:
    """
    Auto-detect store from PDF content.

    Args:
        pdf_content: PDF file as bytes

    Returns:
        Store identifier: 'netto', 'rema', 'foetex', 'bilka', etc.
    """
    try:
        import fitz
        doc = fitz.open(stream=pdf_content, filetype="pdf")

        # Check first 3 pages for better detection
        all_text = ""
        for i in range(min(3, len(doc))):
            all_text += doc[i].get_text().lower() + " "
        doc.close()

        # Score each store based on keyword matches
        store_scores = {}

        for store_id, patterns in STORE_PATTERNS.items():
            # Check exclusive keywords first (high confidence)
            for keyword in patterns.get('exclusive', []):
                if keyword in all_text:
                    logger.info(f"Store detected by exclusive keyword '{keyword}': {store_id}")
                    return store_id

            # Count regular keyword matches
            score = sum(1 for kw in patterns['keywords'] if kw in all_text)
            if score > 0:
                store_scores[store_id] = score

        # Return store with highest score
        if store_scores:
            best_store = max(store_scores.items(), key=lambda x: x[1])
            logger.info(f"Store detected by score ({best_store[1]} matches): {best_store[0]}")
            return best_store[0]

        logger.warning("No store detected, defaulting to 'netto'")
        return 'netto'

    except Exception as e:
        logger.warning(f"Store detection failed: {e}, defaulting to netto")
        return 'netto'


def get_scanner(store: str):
    """
    Get the appropriate scanner for a store.

    Args:
        store: Store identifier (netto, rema, foetex, bilka, etc.)

    Returns:
        Scanner instance
    """
    store_lower = store.lower() if store else 'netto'

    # Get scanner type from store patterns
    scanner_type = 'netto'  # Default
    if store_lower in STORE_PATTERNS:
        scanner_type = STORE_PATTERNS[store_lower].get('scanner', 'netto')

    if scanner_type == 'rema':
        logger.info(f"Using RemaScanner for store: {store_lower}")
        return RemaScanner()
    else:
        logger.info(f"Using NettoScanner for store: {store_lower}")
        return NettoScanner()
