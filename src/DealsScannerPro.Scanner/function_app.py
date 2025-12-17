"""
DealsScannerPro - Event Grid Triggered Scanner Function v2.0
============================================================

Automatically processes PDF flyers when uploaded to blob storage.
Uses the new scanner pipeline with:
- Azure Document Intelligence for layout extraction
- GPT-4o-mini for product normalization
- Deterministic unit price calculation
- Confidence-based auto-publish

Flow:
1. PDF uploaded to 'tilbudsaviser' container
2. Event Grid triggers this function (<1 second latency)
3. Function downloads PDF from blob storage
4. New scanner pipeline extracts and normalizes offers
5. Results are POSTed to the API (v2 format)
6. PDF is moved to 'processed' or 'failed' container
"""

import azure.functions as func
import logging
import json
import re
import os

# Create app first to ensure function discovery works
app = func.FunctionApp()

# Lazy-loaded modules
_requests = None
_BlobServiceClient = None
_datetime = None
_timedelta = None
_Scanner = None
SCANNER_AVAILABLE = False


def _lazy_import():
    """Lazy import of dependencies to avoid blocking function discovery."""
    global _requests, _BlobServiceClient, _datetime, _timedelta
    global _Scanner, SCANNER_AVAILABLE

    if _requests is None:
        try:
            import requests as req_module
            _requests = req_module
        except ImportError as e:
            logging.error(f"Failed to import requests: {e}")

    if _BlobServiceClient is None:
        try:
            from azure.storage.blob import BlobServiceClient
            _BlobServiceClient = BlobServiceClient
        except ImportError as e:
            logging.error(f"Failed to import azure.storage.blob: {e}")

    if _datetime is None:
        try:
            from datetime import datetime, timedelta
            _datetime = datetime
            _timedelta = timedelta
        except ImportError as e:
            logging.error(f"Failed to import datetime: {e}")

    if _Scanner is None:
        try:
            from services.scanner import Scanner
            _Scanner = Scanner
            SCANNER_AVAILABLE = True
            logging.info("Successfully imported scanner v2.0 services")
        except ImportError as e:
            logging.warning(f"Scanner v2.0 not available: {e}")
            # Try legacy scanners as fallback
            try:
                from scanners import detect_store, get_scanner
                logging.info("Falling back to legacy scanners")
            except ImportError:
                logging.error("No scanner available")


# Configuration
API_BASE_URL = os.environ.get("DEALS_API_URL", "https://func-dealscanner-prod.azurewebsites.net")
API_KEY = os.environ.get("DEALS_API_KEY", "")
STORAGE_CONNECTION = os.environ.get("AzureWebJobsStorage", "")
# Support both standard OpenAI and Azure OpenAI
OPENAI_API_KEY = os.environ.get("OPENAI_API_KEY") or os.environ.get("AZURE_OPENAI_API_KEY", "")
OPENAI_ENDPOINT = os.environ.get("AZURE_OPENAI_ENDPOINT", "")  # Only needed for Azure OpenAI


# Health check endpoint (anonymous for easy testing)
@app.route(route="health", methods=["GET"], auth_level=func.AuthLevel.ANONYMOUS)
def health_check(req: func.HttpRequest) -> func.HttpResponse:
    """Health check to verify function deployment."""
    _lazy_import()
    return func.HttpResponse(
        json.dumps({
            "status": "healthy",
            "scanner_v2_available": SCANNER_AVAILABLE,
            "version": "2.0.0"
        }),
        mimetype="application/json",
        status_code=200
    )


# Manual scan endpoint (for testing)
@app.route(route="scan", methods=["POST"])
def manual_scan(req: func.HttpRequest) -> func.HttpResponse:
    """
    Manual scan endpoint for testing.

    POST /api/scan
    Body: PDF file as binary
    Query params: ?butik=netto&gyldig_fra=2025-01-01&gyldig_til=2025-01-07
    """
    _lazy_import()

    try:
        pdf_content = req.get_body()
        if not pdf_content:
            return func.HttpResponse(
                json.dumps({"error": "No PDF content provided"}),
                status_code=400
            )

        # Parse query params
        butik = req.params.get("butik", "unknown")
        gyldig_fra = req.params.get("gyldig_fra")
        gyldig_til = req.params.get("gyldig_til")

        # Scan PDF
        result = scan_pdf_v2(pdf_content, butik)

        return func.HttpResponse(
            json.dumps({
                "success": True,
                "retailer": result.retailer,
                "offers_count": len(result.offers),
                "offers": [offer_to_dict(o) for o in result.offers[:10]],  # First 10
                "metadata": {
                    "total_pages": result.total_pages,
                    "total_blocks": result.total_blocks,
                    "scanner_version": result.scanner_version
                }
            }, ensure_ascii=False, default=str),
            mimetype="application/json",
            status_code=200
        )

    except Exception as e:
        logging.exception(f"Manual scan error: {e}")
        return func.HttpResponse(
            json.dumps({"error": str(e)}),
            status_code=500
        )


@app.event_grid_trigger(arg_name="event")
def process_tilbudsavis(event: func.EventGridEvent):
    """
    Process uploaded PDF flyer via Event Grid trigger.

    Triggered by BlobCreated events on 'tilbudsaviser' container.
    Expected filename format: {butik}_{year}-uge{week}.pdf
    """
    _lazy_import()
    logging.info(f"Event Grid trigger fired: {event.event_type}")
    logging.info(f"Event subject: {event.subject}")

    # Only process BlobCreated events
    if event.event_type != "Microsoft.Storage.BlobCreated":
        logging.info(f"Ignoring event type: {event.event_type}")
        return

    event_data = event.get_json()

    # Extract blob URL and validate container
    blob_url = event_data.get("url", "")
    if "/tilbudsaviser/" not in blob_url:
        logging.info(f"Ignoring blob not in tilbudsaviser container: {blob_url}")
        return

    # Extract filename
    subject = event.subject
    filename = subject.split("/blobs/")[-1] if "/blobs/" in subject else None

    if not filename or not filename.lower().endswith('.pdf'):
        logging.info(f"Ignoring non-PDF file: {filename}")
        return

    logging.info(f"Processing PDF: {filename}")

    try:
        # Parse filename to extract store and week
        butik, gyldig_fra, gyldig_til = parse_filename(filename)
        logging.info(f"Parsed: butik={butik}, fra={gyldig_fra}, til={gyldig_til}")

        # Download PDF content
        pdf_content = download_blob(filename)
        if not pdf_content:
            logging.error(f"Failed to download blob: {filename}")
            move_blob(filename, "failed", {"error": "Download failed"})
            return

        logging.info(f"Downloaded {len(pdf_content)} bytes")

        # Scan with new pipeline
        scan_result = scan_pdf_v2(pdf_content, butik)

        # Use scanner's detected values if available
        if scan_result.retailer:
            butik = scan_result.retailer
        if scan_result.valid_from:
            gyldig_fra = scan_result.valid_from
        if scan_result.valid_to:
            gyldig_til = scan_result.valid_to

        logging.info(f"Extracted {len(scan_result.offers)} offers")

        if not scan_result.offers:
            logging.warning(f"No offers extracted from {filename}")
            move_blob(filename, "failed", {"error": "No offers extracted"})
            return

        # Upload to API (v2 format)
        success = upload_to_api_v2(
            butik=butik,
            gyldig_fra=gyldig_fra,
            gyldig_til=gyldig_til,
            kilde_fil=filename,
            scan_result=scan_result
        )

        if success:
            # Count auto-published vs needs_review
            auto_published = sum(1 for o in scan_result.offers if o.status == "published")
            needs_review = sum(1 for o in scan_result.offers if o.status == "needs_review")

            logging.info(f"Uploaded: {auto_published} auto-published, {needs_review} needs review")
            move_blob(filename, "processed", {
                "processedAt": _datetime.utcnow().isoformat(),
                "offersExtracted": str(len(scan_result.offers)),
                "autoPublished": str(auto_published),
                "needsReview": str(needs_review),
                "scannerVersion": scan_result.scanner_version
            })
        else:
            logging.error(f"Failed to upload offers for {filename}")
            move_blob(filename, "failed", {"error": "API upload failed"})

    except ValueError as e:
        logging.error(f"Invalid filename format: {filename} - {e}")
        move_blob(filename, "failed", {"error": str(e)})
    except Exception as e:
        logging.exception(f"Error processing {filename}: {e}")
        move_blob(filename, "failed", {"error": str(e)})


def scan_pdf_v2(pdf_content: bytes, butik: str):
    """
    Scan PDF using v2.0 pipeline.

    Returns ScanResult with all extracted offers.
    """
    if not SCANNER_AVAILABLE or _Scanner is None:
        logging.error("Scanner v2.0 not available")
        raise RuntimeError("Scanner not available")

    # Initialize scanner with OpenAI credentials
    scanner = _Scanner(
        openai_api_key=OPENAI_API_KEY,
        openai_endpoint=OPENAI_ENDPOINT,
        enable_cropping=True  # Enable bbox cropping for Review UI
    )

    # Run scan
    result = scanner.scan(pdf_content, source_file=butik)

    return result


def offer_to_dict(offer) -> dict:
    """Convert ScannedOffer to dictionary for API upload."""
    return {
        # Raw text
        "product_text_raw": offer.product_text_raw,

        # Normalized fields
        "brand_norm": offer.brand_norm,
        "product_norm": offer.product_norm,
        "variant_norm": offer.variant_norm,
        "category": offer.category,

        # Amount
        "net_amount_value": offer.net_amount_value,
        "net_amount_unit": offer.net_amount_unit,
        "pack_count": offer.pack_count,
        "container_type": offer.container_type,

        # Price
        "price_value": offer.price_value,
        "deposit_value": offer.deposit_value,
        "price_excl_deposit": offer.price_excl_deposit,
        "unit_price_value": offer.unit_price_value,
        "unit_price_unit": offer.unit_price_unit,

        # Identity
        "sku_key": offer.sku_key,

        # Comment
        "comment": offer.comment,

        # Confidence
        "confidence": offer.confidence,
        "confidence_details": offer.confidence_details,
        "confidence_reasons": offer.confidence_reasons,
        "status": offer.status,

        # Visual
        "crop_url": offer.crop_url,

        # Trace
        "trace": offer.trace
    }


def upload_to_api_v2(butik: str, gyldig_fra: str, gyldig_til: str,
                     kilde_fil: str, scan_result, max_retries: int = 3) -> bool:
    """Upload scan results to API using v2 format with retry logic."""

    if not API_KEY:
        logging.error("DEALS_API_KEY not configured")
        return False

    if _requests is None:
        logging.error("requests module not available")
        return False

    # Build v2 payload
    payload = {
        "version": "2.0",
        "meta": {
            "retailer": butik,
            "valid_from": gyldig_fra,
            "valid_to": gyldig_til,
            "source_file": kilde_fil,
            "retailer_confidence": scan_result.retailer_confidence,
            "validity_confidence": scan_result.validity_confidence
        },
        "scan_stats": {
            "total_pages": scan_result.total_pages,
            "total_blocks": scan_result.total_blocks,
            "offers_detected": scan_result.offers_detected,
            "offers_extracted": len(scan_result.offers),
            "scanner_version": scan_result.scanner_version
        },
        "offers": [offer_to_dict(o) for o in scan_result.offers]
    }

    # Log summary
    logging.info(f"Uploading {len(scan_result.offers)} offers for {butik}")

    # Log first few offers for debugging
    for i, offer in enumerate(scan_result.offers[:3]):
        logging.info(
            f"  [{i}] {offer.product_norm} - {offer.price_value} kr "
            f"(conf={offer.confidence:.2f}, status={offer.status})"
        )

    import time

    last_error = None
    for attempt in range(max_retries):
        try:
            if attempt > 0:
                wait_time = 2 ** attempt  # Exponential backoff: 2, 4, 8 seconds
                logging.info(f"Retry {attempt}/{max_retries} after {wait_time}s...")
                time.sleep(wait_time)

            response = _requests.post(
                f"{API_BASE_URL}/api/management/upload/v2",
                json=payload,
                headers={
                    "Content-Type": "application/json",
                    "x-api-key": API_KEY
                },
                timeout=120  # Longer timeout for large payloads
            )

            if response.status_code == 200:
                result = response.json()
                logging.info(f"API SUCCESS: {result}")
                return True
            elif response.status_code == 404:
                # Fallback to v1 endpoint if v2 not available
                logging.info("V2 endpoint not found, trying v1 fallback...")
                return upload_to_api_v1_fallback(butik, gyldig_fra, gyldig_til,
                                                  kilde_fil, scan_result)
            elif response.status_code >= 500:
                # Server error - retry
                last_error = f"Server error: {response.status_code}"
                logging.warning(f"API server error (attempt {attempt+1}): {response.status_code}")
                continue
            else:
                # Client error - don't retry
                logging.error(f"API client error: {response.status_code} - {response.text}")
                return False

        except _requests.exceptions.Timeout:
            last_error = "Request timeout"
            logging.warning(f"API timeout (attempt {attempt+1}), trying v1 fallback...")
            # V2 endpoint times out, fallback to v1
            return upload_to_api_v1_fallback(butik, gyldig_fra, gyldig_til,
                                              kilde_fil, scan_result)
        except _requests.exceptions.ConnectionError as e:
            last_error = f"Connection error: {e}"
            logging.warning(f"API connection error (attempt {attempt+1}): {e}")
            continue
        except Exception as e:
            last_error = str(e)
            logging.exception(f"Unexpected error calling API: {e}")
            return False

    logging.error(f"API upload failed after {max_retries} attempts: {last_error}")
    return False


def upload_to_api_v1_fallback(butik: str, gyldig_fra: str, gyldig_til: str,
                               kilde_fil: str, scan_result) -> bool:
    """Fallback to v1 API format for backwards compatibility."""

    # Convert v2 offers to v1 format
    tilbud_v1 = []
    for offer in scan_result.offers:
        tilbud_v1.append({
            "produkt": offer.product_norm or offer.product_text_raw,
            "total_pris": offer.price_value,
            "pris_per_enhed": offer.unit_price_value,
            "enhed": offer.unit_price_unit or "stk",
            "maengde": f"{offer.net_amount_value} {offer.net_amount_unit}" if offer.net_amount_value else None,
            "kategori": offer.category,
            "konfidens": offer.confidence,
            "side": offer.trace.get("page", 0) + 1 if offer.trace else 1
        })

    payload = {
        "meta": {
            "butik": butik,
            "gyldig_fra": gyldig_fra,
            "gyldig_til": gyldig_til,
            "kilde_fil": kilde_fil
        },
        "statistik": {
            "antal_tilbud": len(tilbud_v1),
            "hoj_konfidens": sum(1 for t in tilbud_v1 if t.get("konfidens", 0) >= 0.9)
        },
        "tilbud": tilbud_v1
    }

    try:
        response = _requests.post(
            f"{API_BASE_URL}/api/management/upload",
            json=payload,
            headers={
                "Content-Type": "application/json",
                "x-api-key": API_KEY
            },
            timeout=60
        )

        if response.status_code == 200:
            logging.info(f"V1 fallback SUCCESS: {response.json()}")
            return True
        else:
            logging.error(f"V1 fallback error: {response.status_code}")
            return False

    except Exception as e:
        logging.exception(f"V1 fallback error: {e}")
        return False


def download_blob(filename: str) -> bytes:
    """Download blob content from tilbudsaviser container."""
    if not STORAGE_CONNECTION or _BlobServiceClient is None:
        logging.error("Blob storage not configured")
        return None

    try:
        blob_service = _BlobServiceClient.from_connection_string(STORAGE_CONNECTION)
        container_client = blob_service.get_container_client("tilbudsaviser")
        blob_client = container_client.get_blob_client(filename)
        return blob_client.download_blob().readall()

    except Exception as e:
        logging.exception(f"Error downloading blob: {e}")
        return None


def parse_filename(filename: str) -> tuple:
    """
    Parse filename to extract store and validity period.
    Format: {butik}_{year}-uge{week}.pdf
    """
    name = filename.lower().replace('.pdf', '')

    match = re.match(r'^([a-z0-9]+)_(\d{4})-uge(\d{1,2})$', name)
    if not match:
        raise ValueError(f"Invalid format. Expected: butik_year-ugeXX.pdf")

    butik = match.group(1)
    year = int(match.group(2))
    week = int(match.group(3))

    # Validate store
    valid_stores = ['netto', 'rema', 'foetex', 'bilka', 'superbrugsen', 'spar', '365discount']
    if butik not in valid_stores:
        raise ValueError(f"Unknown store: {butik}")

    # Calculate week dates
    jan4 = _datetime(year, 1, 4)
    week_start = jan4 - _timedelta(days=jan4.weekday())
    gyldig_fra = week_start + _timedelta(weeks=week - 1)
    gyldig_til = gyldig_fra + _timedelta(days=6)

    return butik, gyldig_fra.strftime('%Y-%m-%d'), gyldig_til.strftime('%Y-%m-%d')


def move_blob(filename: str, destination: str, metadata: dict = None):
    """Move blob to processed or failed container."""
    if not STORAGE_CONNECTION or _BlobServiceClient is None:
        logging.warning("Blob storage not configured")
        return

    try:
        blob_service = _BlobServiceClient.from_connection_string(STORAGE_CONNECTION)

        # Source
        source_container = blob_service.get_container_client("tilbudsaviser")
        source_blob = source_container.get_blob_client(filename)

        # Destination with folder structure
        now = _datetime.utcnow()
        dest_path = f"{now.year}/uge{now.isocalendar()[1]:02d}/{filename}"

        dest_container = blob_service.get_container_client(destination)
        dest_blob = dest_container.get_blob_client(dest_path)

        # Copy and delete
        dest_blob.start_copy_from_url(source_blob.url)
        if metadata:
            dest_blob.set_blob_metadata(metadata)
        source_blob.delete_blob()

        logging.info(f"Moved {filename} to {destination}/{dest_path}")

    except Exception as e:
        logging.exception(f"Error moving blob: {e}")
