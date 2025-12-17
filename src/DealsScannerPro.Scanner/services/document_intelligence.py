"""
Azure Document Intelligence Service
====================================

Uses Azure AI Document Intelligence (Layout model) to extract
structured text and bounding boxes from PDF flyers.

Layout model provides:
- Text extraction with position (bbox)
- Paragraph detection
- Table detection
- Reading order
"""

import os
import logging
from dataclasses import dataclass, field
from typing import List, Optional, Tuple

logger = logging.getLogger(__name__)


@dataclass
class TextBlock:
    """A block of text extracted from a page."""
    text: str
    bbox: Tuple[float, float, float, float]  # (x1, y1, x2, y2) normalized 0-1
    page: int
    confidence: float = 1.0

    @property
    def x1(self) -> float:
        return self.bbox[0]

    @property
    def y1(self) -> float:
        return self.bbox[1]

    @property
    def x2(self) -> float:
        return self.bbox[2]

    @property
    def y2(self) -> float:
        return self.bbox[3]

    @property
    def width(self) -> float:
        return self.x2 - self.x1

    @property
    def height(self) -> float:
        return self.y2 - self.y1

    @property
    def center_x(self) -> float:
        return (self.x1 + self.x2) / 2

    @property
    def center_y(self) -> float:
        return (self.y1 + self.y2) / 2


@dataclass
class PageLayout:
    """Layout information for a single page."""
    page_number: int
    width: float  # in inches
    height: float  # in inches
    blocks: List[TextBlock] = field(default_factory=list)

    def get_blocks_in_region(
        self,
        x1: float, y1: float,
        x2: float, y2: float
    ) -> List[TextBlock]:
        """Get all blocks that overlap with the given region."""
        result = []
        for block in self.blocks:
            # Check if block overlaps with region
            if (block.x1 < x2 and block.x2 > x1 and
                block.y1 < y2 and block.y2 > y1):
                result.append(block)
        return result


@dataclass
class DocumentLayout:
    """Complete layout information for a document."""
    pages: List[PageLayout] = field(default_factory=list)
    retailer_detected: Optional[str] = None
    retailer_confidence: float = 0.0
    validity_period: Optional[Tuple[str, str]] = None  # (from, to)
    validity_confidence: float = 0.0

    @property
    def total_pages(self) -> int:
        return len(self.pages)

    @property
    def all_blocks(self) -> List[TextBlock]:
        """Get all text blocks across all pages."""
        blocks = []
        for page in self.pages:
            blocks.extend(page.blocks)
        return blocks


class DocumentIntelligenceService:
    """
    Service for extracting layout from PDFs using Azure Document Intelligence.
    """

    def __init__(
        self,
        endpoint: Optional[str] = None,
        api_key: Optional[str] = None
    ):
        """
        Initialize the Document Intelligence service.

        Args:
            endpoint: Azure Document Intelligence endpoint URL
            api_key: API key for authentication
        """
        self.endpoint = endpoint or os.environ.get("DOCUMENT_INTELLIGENCE_ENDPOINT")
        self.api_key = api_key or os.environ.get("DOCUMENT_INTELLIGENCE_KEY")

        if not self.endpoint or not self.api_key:
            logger.warning(
                "Document Intelligence credentials not configured. "
                "Set DOCUMENT_INTELLIGENCE_ENDPOINT and DOCUMENT_INTELLIGENCE_KEY."
            )

        self._client = None

    def _get_client(self):
        """Lazy initialization of Document Intelligence client."""
        if self._client is None:
            if not self.endpoint or not self.api_key:
                raise ValueError("Document Intelligence credentials not configured")

            from azure.ai.documentintelligence import DocumentIntelligenceClient
            from azure.core.credentials import AzureKeyCredential

            self._client = DocumentIntelligenceClient(
                endpoint=self.endpoint,
                credential=AzureKeyCredential(self.api_key)
            )
        return self._client

    def analyze_pdf(self, pdf_content: bytes) -> DocumentLayout:
        """
        Analyze a PDF and extract layout information.

        Args:
            pdf_content: PDF file as bytes

        Returns:
            DocumentLayout with pages and text blocks
        """
        import base64
        client = self._get_client()

        logger.info("Sending PDF to Document Intelligence...")

        # Use the prebuilt-layout model for best results with flyers
        # New SDK requires base64-encoded content in body
        from azure.ai.documentintelligence.models import AnalyzeDocumentRequest

        poller = client.begin_analyze_document(
            model_id="prebuilt-layout",
            body=AnalyzeDocumentRequest(bytes_source=base64.b64encode(pdf_content).decode('utf-8'))
        )

        result = poller.result()

        logger.info(f"Document Intelligence completed: {len(result.pages)} pages")

        # Convert to our data structures
        document = DocumentLayout()

        for page in result.pages:
            page_layout = PageLayout(
                page_number=page.page_number,
                width=page.width or 8.5,  # Default to letter size
                height=page.height or 11.0
            )

            # Extract paragraphs as text blocks
            if hasattr(result, 'paragraphs') and result.paragraphs:
                for para in result.paragraphs:
                    # Check if paragraph is on this page
                    if not para.bounding_regions:
                        continue

                    for region in para.bounding_regions:
                        if region.page_number != page.page_number:
                            continue

                        # Convert polygon to bbox
                        bbox = self._polygon_to_bbox(
                            region.polygon,
                            page_layout.width,
                            page_layout.height
                        )

                        block = TextBlock(
                            text=para.content,
                            bbox=bbox,
                            page=page.page_number,
                            confidence=1.0  # Paragraphs don't have confidence
                        )
                        page_layout.blocks.append(block)

            # If no paragraphs, fall back to lines
            if not page_layout.blocks and page.lines:
                for line in page.lines:
                    if not line.polygon:
                        continue

                    bbox = self._polygon_to_bbox(
                        line.polygon,
                        page_layout.width,
                        page_layout.height
                    )

                    block = TextBlock(
                        text=line.content,
                        bbox=bbox,
                        page=page.page_number,
                        confidence=1.0
                    )
                    page_layout.blocks.append(block)

            document.pages.append(page_layout)

        # Try to detect retailer and validity from first page
        if document.pages:
            first_page_text = " ".join(
                b.text for b in document.pages[0].blocks[:10]
            ).lower()

            document.retailer_detected, document.retailer_confidence = \
                self._detect_retailer(first_page_text)

            document.validity_period, document.validity_confidence = \
                self._detect_validity(first_page_text)

        logger.info(
            f"Extracted {sum(len(p.blocks) for p in document.pages)} text blocks, "
            f"detected retailer: {document.retailer_detected}"
        )

        return document

    def _polygon_to_bbox(
        self,
        polygon: List[float],
        page_width: float,
        page_height: float
    ) -> Tuple[float, float, float, float]:
        """
        Convert polygon points to normalized bounding box.

        Polygon format: [x1, y1, x2, y2, x3, y3, x4, y4] (4 corners)
        Returns: (x1, y1, x2, y2) normalized to 0-1
        """
        if len(polygon) < 8:
            return (0, 0, 1, 1)  # Fallback to full page

        # Extract x and y coordinates
        x_coords = [polygon[i] for i in range(0, len(polygon), 2)]
        y_coords = [polygon[i] for i in range(1, len(polygon), 2)]

        # Get bounding box
        x1 = min(x_coords)
        y1 = min(y_coords)
        x2 = max(x_coords)
        y2 = max(y_coords)

        # Normalize to 0-1 range
        return (
            x1 / page_width,
            y1 / page_height,
            x2 / page_width,
            y2 / page_height
        )

    def _detect_retailer(self, text: str) -> Tuple[Optional[str], float]:
        """
        Detect retailer from text content.

        Returns: (retailer_id, confidence)
        """
        # Retailers with their detection keywords
        # Ordered by specificity (most specific first)
        retailers = {
            # Specific names first (high confidence)
            'rema': (['rema 1000', 'rema1000'], 0.98),
            'foetex': (['føtex', 'foetex'], 0.98),
            'bilka': (['bilka'], 0.98),
            'superbrugsen': (['superbrugsen', 'super brugsen'], 0.98),
            'kvickly': (['kvickly'], 0.98),
            '365discount': (['365discount', '365 discount', 'coop 365'], 0.98),
            'lidl': (['lidl'], 0.98),
            'aldi': (['aldi'], 0.98),
            'meny': (['meny'], 0.95),
            'irma': (['irma'], 0.95),
            'spar': (['eurospar'], 0.98),  # eurospar is more specific
            'netto': (['netto'], 0.95),  # Check after others (common word)
            # Less specific matches
            'spar': (['spar '], 0.90),  # With space to avoid false matches
            'rema': (['rema'], 0.90),  # Fallback for 'rema' alone
        }

        text_lower = text.lower()

        # First pass: exact/specific matches
        for retailer_id, (keywords, confidence) in retailers.items():
            for keyword in keywords:
                if keyword in text_lower:
                    return (retailer_id, confidence)

        # Second pass: look for Salling Group stores
        if 'salling' in text_lower:
            # Could be Netto, Føtex, or Bilka
            return ('netto', 0.70)  # Default to Netto with lower confidence

        return (None, 0.0)

    def _detect_validity(self, text: str) -> Tuple[Optional[Tuple[str, str]], float]:
        """
        Detect validity period from text content.

        Looks for patterns like:
        - "Uge 51" -> calculate Monday-Sunday
        - "15/12 - 21/12" -> parse dates
        - "Gyldig fra 15. december" -> parse date

        Returns: ((from_date, to_date), confidence)
        """
        import re
        from datetime import datetime, timedelta

        text_lower = text.lower()

        # Pattern 1: "Uge XX" or "UGE XX"
        week_match = re.search(r'uge\s*(\d{1,2})', text_lower)
        if week_match:
            week_num = int(week_match.group(1))
            year = datetime.now().year

            # Calculate week start (Monday) and end (Sunday)
            jan4 = datetime(year, 1, 4)
            week_start = jan4 - timedelta(days=jan4.weekday())
            valid_from = week_start + timedelta(weeks=week_num - 1)
            valid_to = valid_from + timedelta(days=6)

            return (
                (valid_from.strftime('%Y-%m-%d'), valid_to.strftime('%Y-%m-%d')),
                0.85
            )

        # Pattern 2: "DD/MM - DD/MM" or "DD.MM - DD.MM"
        date_range_match = re.search(
            r'(\d{1,2})[./](\d{1,2})\s*[-–]\s*(\d{1,2})[./](\d{1,2})',
            text
        )
        if date_range_match:
            year = datetime.now().year
            try:
                from_day = int(date_range_match.group(1))
                from_month = int(date_range_match.group(2))
                to_day = int(date_range_match.group(3))
                to_month = int(date_range_match.group(4))

                valid_from = datetime(year, from_month, from_day)
                valid_to = datetime(year, to_month, to_day)

                # Handle year boundary
                if valid_to < valid_from:
                    valid_to = datetime(year + 1, to_month, to_day)

                return (
                    (valid_from.strftime('%Y-%m-%d'), valid_to.strftime('%Y-%m-%d')),
                    0.90
                )
            except ValueError:
                pass

        return (None, 0.0)


# Fallback implementation using PyMuPDF when Document Intelligence is not available
class PyMuPDFLayoutService:
    """
    Fallback layout extraction using PyMuPDF.
    Used when Document Intelligence is not configured.
    """

    def analyze_pdf(self, pdf_content: bytes) -> DocumentLayout:
        """Extract layout using PyMuPDF."""
        import fitz

        logger.info("Using PyMuPDF fallback for layout extraction")

        document = DocumentLayout()

        doc = fitz.open(stream=pdf_content, filetype="pdf")

        for page_num in range(len(doc)):
            page = doc[page_num]
            rect = page.rect

            page_layout = PageLayout(
                page_number=page_num + 1,
                width=rect.width / 72,  # Convert points to inches
                height=rect.height / 72
            )

            # Extract text blocks with positions
            blocks = page.get_text("dict", flags=fitz.TEXT_PRESERVE_WHITESPACE)["blocks"]

            for block in blocks:
                if block.get("type") != 0:  # Skip non-text blocks
                    continue

                # Combine lines in block
                text_parts = []
                for line in block.get("lines", []):
                    for span in line.get("spans", []):
                        text_parts.append(span.get("text", ""))

                text = " ".join(text_parts).strip()
                if not text:
                    continue

                # Get bbox and normalize
                bbox = block.get("bbox", (0, 0, rect.width, rect.height))
                normalized_bbox = (
                    bbox[0] / rect.width,
                    bbox[1] / rect.height,
                    bbox[2] / rect.width,
                    bbox[3] / rect.height
                )

                text_block = TextBlock(
                    text=text,
                    bbox=normalized_bbox,
                    page=page_num + 1,
                    confidence=0.8  # Lower confidence for fallback
                )
                page_layout.blocks.append(text_block)

            document.pages.append(page_layout)

        doc.close()

        # Detect retailer and validity
        if document.pages:
            first_page_text = " ".join(
                b.text for b in document.pages[0].blocks[:10]
            ).lower()

            di_service = DocumentIntelligenceService()
            document.retailer_detected, document.retailer_confidence = \
                di_service._detect_retailer(first_page_text)
            document.validity_period, document.validity_confidence = \
                di_service._detect_validity(first_page_text)

        logger.info(f"PyMuPDF extracted {sum(len(p.blocks) for p in document.pages)} blocks")

        return document


def get_layout_service() -> DocumentIntelligenceService:
    """
    Get the appropriate layout service based on configuration.
    Returns Document Intelligence if configured, otherwise PyMuPDF fallback.
    """
    endpoint = os.environ.get("DOCUMENT_INTELLIGENCE_ENDPOINT")
    api_key = os.environ.get("DOCUMENT_INTELLIGENCE_KEY")

    if endpoint and api_key:
        logger.info("Using Azure Document Intelligence for layout extraction")
        return DocumentIntelligenceService(endpoint, api_key)
    else:
        logger.info("Document Intelligence not configured, using PyMuPDF fallback")
        return PyMuPDFLayoutService()
