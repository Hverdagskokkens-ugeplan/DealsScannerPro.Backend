"""
DealsScannerPro Scanners
========================

Scanner implementations for Danish supermarket flyers.
"""

import logging
from .netto_scanner import ScannerV2 as NettoScanner
from .rema_scanner import RemaScanner

__all__ = ['NettoScanner', 'RemaScanner', 'detect_store', 'get_scanner']


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

        # Rema 1000 specific patterns
        rema_patterns = [
            'rema', 'rema1000', 'rema 1000',
            'meget mere', 'god pris', 'altid billig',
            'frilandsgris', 'gram slot'
        ]

        # Netto specific patterns
        netto_patterns = [
            'netto', 'døgnnet', 'nettodag'
        ]

        # Count matches for each store
        rema_score = sum(1 for p in rema_patterns if p in all_text)
        netto_score = sum(1 for p in netto_patterns if p in all_text)

        # Check for explicit store names first
        if 'rema' in all_text or 'rema1000' in all_text:
            return 'rema'
        elif 'netto' in all_text:
            return 'netto'
        elif 'føtex' in all_text:
            return 'foetex'
        elif 'bilka' in all_text:
            return 'bilka'
        # Fall back to pattern scoring
        elif rema_score > netto_score:
            return 'rema'
        elif netto_score > 0:
            return 'netto'
        else:
            return 'netto'  # Default
    except Exception as e:
        logging.warning(f"Store detection failed: {e}, defaulting to netto")
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

    if store_lower == 'rema':
        return RemaScanner()
    else:
        # Use Netto scanner as default (works for netto, foetex, bilka, etc.)
        return NettoScanner()
