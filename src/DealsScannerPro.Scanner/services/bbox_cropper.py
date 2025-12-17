"""
Bbox Cropper Service
====================

Extracts bounding box regions from PDF pages as images
and uploads them to Azure Blob Storage for Review UI.

Flow:
1. Load PDF with PyMuPDF
2. Extract page at specified page number
3. Crop to bounding box (normalized 0-1 coords)
4. Save as PNG image
5. Upload to Blob Storage
6. Return blob URL
"""

import io
import os
import logging
import hashlib
from typing import Optional, Tuple
from dataclasses import dataclass

logger = logging.getLogger(__name__)

# Try to import optional dependencies
try:
    import fitz  # PyMuPDF
    HAS_PYMUPDF = True
except ImportError:
    HAS_PYMUPDF = False
    logger.warning("PyMuPDF not available - bbox cropping disabled")

try:
    from azure.storage.blob import BlobServiceClient, ContentSettings
    HAS_AZURE_BLOB = True
except ImportError:
    HAS_AZURE_BLOB = False
    logger.warning("azure-storage-blob not available - blob upload disabled")


@dataclass
class CropResult:
    """Result of bbox cropping."""
    success: bool
    blob_url: Optional[str] = None
    blob_name: Optional[str] = None
    error: Optional[str] = None
    image_bytes: Optional[bytes] = None  # For local testing


class BboxCropper:
    """
    Service for cropping bbox regions from PDFs and uploading to Blob Storage.
    """

    def __init__(
        self,
        connection_string: Optional[str] = None,
        container_name: str = "offer-crops"
    ):
        """
        Initialize the bbox cropper.

        Args:
            connection_string: Azure Storage connection string.
                              Falls back to AZURE_STORAGE_CONNECTION_STRING env var.
            container_name: Blob container name for crops
        """
        self.container_name = container_name
        self.blob_service_client = None

        # Get connection string
        conn_str = connection_string or os.environ.get("AZURE_STORAGE_CONNECTION_STRING")

        if conn_str and HAS_AZURE_BLOB:
            try:
                self.blob_service_client = BlobServiceClient.from_connection_string(conn_str)
                # Ensure container exists
                container_client = self.blob_service_client.get_container_client(container_name)
                if not container_client.exists():
                    container_client.create_container(public_access="blob")
                    logger.info(f"Created blob container: {container_name}")
            except Exception as e:
                logger.warning(f"Failed to initialize blob storage: {e}")
                self.blob_service_client = None

    def crop_and_upload(
        self,
        pdf_content: bytes,
        page: int,
        bbox: Tuple[float, float, float, float],
        offer_id: str,
        dpi: int = 150,
        padding: float = 0.02
    ) -> CropResult:
        """
        Crop bbox from PDF page and upload to Blob Storage.

        Args:
            pdf_content: PDF file as bytes
            page: Page number (0-indexed)
            bbox: Bounding box as (x0, y0, x1, y1) normalized 0-1
            offer_id: Unique ID for naming the blob
            dpi: Resolution for rendering (default 150)
            padding: Extra padding around bbox (default 2%)

        Returns:
            CropResult with blob URL or error
        """
        if not HAS_PYMUPDF:
            return CropResult(
                success=False,
                error="PyMuPDF not installed"
            )

        try:
            # Open PDF
            doc = fitz.open(stream=pdf_content, filetype="pdf")

            if page >= len(doc):
                return CropResult(
                    success=False,
                    error=f"Page {page} does not exist (PDF has {len(doc)} pages)"
                )

            pdf_page = doc[page]

            # Convert normalized bbox to page coordinates
            page_width = pdf_page.rect.width
            page_height = pdf_page.rect.height

            x0, y0, x1, y1 = bbox

            # Add padding
            pad_x = (x1 - x0) * padding
            pad_y = (y1 - y0) * padding

            x0 = max(0, x0 - pad_x)
            y0 = max(0, y0 - pad_y)
            x1 = min(1, x1 + pad_x)
            y1 = min(1, y1 + pad_y)

            # Convert to page coordinates
            clip_rect = fitz.Rect(
                x0 * page_width,
                y0 * page_height,
                x1 * page_width,
                y1 * page_height
            )

            # Calculate matrix for desired DPI
            zoom = dpi / 72  # PDF default is 72 DPI
            matrix = fitz.Matrix(zoom, zoom)

            # Render clipped area
            pix = pdf_page.get_pixmap(matrix=matrix, clip=clip_rect)

            # Convert to PNG bytes
            image_bytes = pix.tobytes("png")

            doc.close()

            # Generate blob name
            blob_name = f"{offer_id}.png"

            # Upload to blob storage if available
            if self.blob_service_client:
                try:
                    blob_client = self.blob_service_client.get_blob_client(
                        container=self.container_name,
                        blob=blob_name
                    )

                    blob_client.upload_blob(
                        image_bytes,
                        overwrite=True,
                        content_settings=ContentSettings(content_type="image/png")
                    )

                    blob_url = blob_client.url

                    logger.debug(f"Uploaded crop to {blob_url}")

                    return CropResult(
                        success=True,
                        blob_url=blob_url,
                        blob_name=blob_name,
                        image_bytes=image_bytes
                    )

                except Exception as e:
                    logger.warning(f"Failed to upload crop: {e}")
                    return CropResult(
                        success=True,  # Crop succeeded, upload failed
                        error=f"Upload failed: {e}",
                        image_bytes=image_bytes
                    )
            else:
                # No blob storage - return image bytes only
                return CropResult(
                    success=True,
                    image_bytes=image_bytes,
                    error="Blob storage not configured"
                )

        except Exception as e:
            logger.error(f"Failed to crop bbox: {e}")
            return CropResult(
                success=False,
                error=str(e)
            )

    def generate_offer_id(
        self,
        retailer: str,
        page: int,
        bbox: Tuple[float, float, float, float],
        product_text: str
    ) -> str:
        """
        Generate a deterministic offer ID for blob naming.

        Args:
            retailer: Retailer name
            page: Page number
            bbox: Bounding box
            product_text: Product text

        Returns:
            Deterministic ID string
        """
        # Create deterministic hash from inputs
        content = f"{retailer}|{page}|{bbox}|{product_text[:50]}"
        hash_value = hashlib.sha256(content.encode()).hexdigest()[:12]
        return f"{retailer.lower()}-p{page}-{hash_value}"


def get_cropper() -> BboxCropper:
    """Get a configured BboxCropper instance."""
    return BboxCropper()


# Example usage and tests
if __name__ == "__main__":
    print("Bbox Cropper Tests")
    print("=" * 60)

    # Check dependencies
    print(f"\nPyMuPDF available: {HAS_PYMUPDF}")
    print(f"Azure Blob available: {HAS_AZURE_BLOB}")

    if HAS_PYMUPDF:
        # Test with a simple PDF
        import fitz

        # Create a simple test PDF
        doc = fitz.open()
        page = doc.new_page(width=595, height=842)  # A4

        # Add some text
        page.insert_text((100, 100), "Test Product", fontsize=24)
        page.insert_text((100, 140), "29.95 kr", fontsize=18)
        page.insert_text((100, 170), "500 g", fontsize=14)

        # Draw a rectangle around it
        rect = fitz.Rect(80, 70, 300, 200)
        page.draw_rect(rect, color=(1, 0, 0), width=2)

        pdf_bytes = doc.tobytes()
        doc.close()

        # Test cropping
        cropper = BboxCropper()

        # Bbox in normalized coordinates (0-1)
        bbox = (80/595, 70/842, 300/595, 200/842)

        result = cropper.crop_and_upload(
            pdf_bytes,
            page=0,
            bbox=bbox,
            offer_id="test-offer-001"
        )

        print(f"\nCrop result:")
        print(f"  Success: {result.success}")
        print(f"  Blob URL: {result.blob_url}")
        print(f"  Error: {result.error}")
        print(f"  Image size: {len(result.image_bytes) if result.image_bytes else 0} bytes")

        # Save locally for inspection
        if result.image_bytes:
            with open("test_crop.png", "wb") as f:
                f.write(result.image_bytes)
            print(f"  Saved to: test_crop.png")

        # Test ID generation
        offer_id = cropper.generate_offer_id(
            retailer="Netto",
            page=0,
            bbox=bbox,
            product_text="Test Product"
        )
        print(f"\nGenerated offer ID: {offer_id}")
