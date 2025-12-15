"""
DealsScannerPro - Event Grid Triggered Scanner Function
=======================================================

Automatically processes PDF flyers when uploaded to blob storage.
Uses Event Grid for reliable, low-latency triggering.

Flow:
1. PDF uploaded to 'tilbudsaviser' container
2. Event Grid triggers this function (<1 second latency)
3. Function downloads PDF from blob storage
4. Scanner extracts deals from PDF (using production scanners)
5. Results are POSTed to the API
6. PDF is moved to 'processed' or 'failed' container
"""

import azure.functions as func
import logging
import json
import re
import os

# Create app first to ensure function discovery works
app = func.FunctionApp()

# Lazy-loaded modules (imported when needed)
_requests = None
_BlobServiceClient = None
_tempfile = None
_datetime = None
_timedelta = None
_detect_store = None
_get_scanner = None
SCANNERS_AVAILABLE = False


def _lazy_import():
    """Lazy import of dependencies to avoid blocking function discovery."""
    global _requests, _BlobServiceClient, _tempfile, _datetime, _timedelta
    global _detect_store, _get_scanner, SCANNERS_AVAILABLE

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

    if _tempfile is None:
        try:
            import tempfile as tf_module
            _tempfile = tf_module
        except ImportError as e:
            logging.error(f"Failed to import tempfile: {e}")

    if _datetime is None:
        try:
            from datetime import datetime, timedelta
            _datetime = datetime
            _timedelta = timedelta
        except ImportError as e:
            logging.error(f"Failed to import datetime: {e}")

    if _detect_store is None:
        try:
            from scanners import detect_store, get_scanner
            _detect_store = detect_store
            _get_scanner = get_scanner
            SCANNERS_AVAILABLE = True
            logging.info("Successfully imported production scanners")
        except ImportError as e:
            logging.error(f"Failed to import scanners: {e}")


# Health check endpoint for testing deployment
@app.route(route="health", methods=["GET"])
def health_check(req: func.HttpRequest) -> func.HttpResponse:
    """Simple health check to verify function deployment."""
    _lazy_import()
    return func.HttpResponse(
        '{"status":"healthy","scanners_available":' + str(SCANNERS_AVAILABLE).lower() + '}',
        mimetype="application/json",
        status_code=200
    )


# Configuration
API_BASE_URL = os.environ.get("DEALS_API_URL", "https://func-dealscanner-prod.azurewebsites.net")
API_KEY = os.environ.get("DEALS_API_KEY", "")
STORAGE_CONNECTION = os.environ.get("AzureWebJobsStorage", "")


@app.event_grid_trigger(arg_name="event")
def process_tilbudsavis(event: func.EventGridEvent):
    """
    Process uploaded PDF flyer via Event Grid trigger.

    Triggered by BlobCreated events on 'tilbudsaviser' container.
    Expected filename format: {butik}_{year}-uge{week}.pdf
    Examples: netto_2025-uge51.pdf, rema_2025-uge51.pdf
    """
    _lazy_import()  # Load dependencies
    logging.info(f"Event Grid trigger fired: {event.event_type}")
    logging.info(f"Event subject: {event.subject}")
    logging.info(f"Event data: {event.get_json()}")

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

    # Extract filename from subject (format: /blobServices/default/containers/tilbudsaviser/blobs/filename.pdf)
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

        # Download PDF content from blob storage
        pdf_content = download_blob(filename)
        if not pdf_content:
            logging.error(f"Failed to download blob: {filename}")
            return

        logging.info(f"Downloaded {len(pdf_content)} bytes from blob")

        # Process PDF and extract deals using production scanners
        tilbud, scan_metadata = extract_deals_from_pdf(pdf_content, butik)
        logging.info(f"Extracted {len(tilbud)} deals from PDF")

        if not tilbud:
            logging.warning(f"No deals extracted from {filename}")
            move_blob(filename, "failed", {"error": "No deals extracted"})
            return

        # Use scanner's validity dates if available (more accurate)
        if scan_metadata.get('gyldig_fra'):
            gyldig_fra = scan_metadata['gyldig_fra']
        if scan_metadata.get('gyldig_til'):
            gyldig_til = scan_metadata['gyldig_til']

        # Upload to API with enhanced metadata
        upload_result = upload_to_api(butik, gyldig_fra, gyldig_til, filename, tilbud, scan_metadata)

        if upload_result:
            logging.info(f"Successfully uploaded {len(tilbud)} deals for {butik}")
            move_blob(filename, "processed", {
                "processedAt": datetime.utcnow().isoformat(),
                "dealsExtracted": str(len(tilbud)),
                "highConfidence": str(scan_metadata.get('hoj_konfidens', 0)),
                "scannerVersion": scan_metadata.get('scanner_version', 'unknown')
            })
        else:
            logging.error(f"Failed to upload deals for {filename}")
            move_blob(filename, "failed", {"error": "API upload failed"})

    except ValueError as e:
        logging.error(f"Invalid filename format: {filename} - {str(e)}")
        move_blob(filename, "failed", {"error": str(e)})
    except Exception as e:
        logging.exception(f"Error processing {filename}: {str(e)}")
        move_blob(filename, "failed", {"error": str(e)})


def download_blob(filename: str) -> bytes:
    """Download blob content from tilbudsaviser container."""
    if not STORAGE_CONNECTION:
        logging.error("Storage connection not configured")
        return None

    if _BlobServiceClient is None:
        logging.error("BlobServiceClient not available")
        return None

    try:
        blob_service = _BlobServiceClient.from_connection_string(STORAGE_CONNECTION)
        container_client = blob_service.get_container_client("tilbudsaviser")
        blob_client = container_client.get_blob_client(filename)

        download_stream = blob_client.download_blob()
        return download_stream.readall()

    except Exception as e:
        logging.exception(f"Error downloading blob {filename}: {str(e)}")
        return None


def parse_filename(filename: str) -> tuple:
    """
    Parse filename to extract store and validity period.

    Expected format: {butik}_{year}-uge{week}.pdf
    Returns: (butik, gyldig_fra, gyldig_til)
    """
    # Remove .pdf extension
    name = filename.lower().replace('.pdf', '')

    # Match pattern: butik_year-ugeXX
    match = re.match(r'^([a-z0-9]+)_(\d{4})-uge(\d{1,2})$', name)
    if not match:
        raise ValueError(f"Invalid filename format. Expected: butik_year-ugeXX.pdf, got: {filename}")

    butik = match.group(1)
    year = int(match.group(2))
    week = int(match.group(3))

    # Validate store
    valid_stores = ['netto', 'rema', 'foetex', 'bilka', 'superbrugsen', 'spar', '365discount']
    if butik not in valid_stores:
        raise ValueError(f"Unknown store: {butik}. Valid stores: {', '.join(valid_stores)}")

    # Calculate week dates (Monday to Sunday)
    # ISO week date: week 1 is the week containing Jan 4
    jan4 = _datetime(year, 1, 4)
    week_start = jan4 - _timedelta(days=jan4.weekday())  # Monday of week 1
    gyldig_fra = week_start + _timedelta(weeks=week - 1)
    gyldig_til = gyldig_fra + _timedelta(days=6)

    return butik, gyldig_fra.strftime('%Y-%m-%d'), gyldig_til.strftime('%Y-%m-%d')


def extract_deals_fallback(pdf_content: bytes, butik: str) -> list:
    """Simple fallback extraction when production scanners are not available."""
    try:
        import fitz
    except ImportError:
        logging.error("PyMuPDF not installed")
        return []

    tilbud = []
    try:
        doc = fitz.open(stream=pdf_content, filetype="pdf")
        logging.info(f"Fallback: PDF has {len(doc)} pages")

        for page_num in range(len(doc)):
            page = doc[page_num]
            text = page.get_text()
            lines = text.split('\n')

            for line in lines:
                line = line.strip()
                if not line or len(line) < 3:
                    continue

                # Simple price pattern matching
                price_match = re.search(r'(\d+)[,.](\d{2})(?:\s*kr)?\.?$|(\d+)\.-$', line)
                if price_match:
                    if price_match.group(3):
                        price = float(price_match.group(3))
                    else:
                        price = float(f"{price_match.group(1)}.{price_match.group(2)}")

                    product_text = line[:price_match.start()].strip()
                    if product_text and len(product_text) > 2 and price > 0:
                        tilbud.append({
                            "produkt": product_text,
                            "total_pris": price,
                            "pris_per_enhed": price,
                            "enhed": "stk",
                            "maengde": "1 stk",
                            "kategori": "Andet",
                            "konfidens": 0.5,  # Low confidence for fallback
                            "side": page_num + 1
                        })

        doc.close()
    except Exception as e:
        logging.exception(f"Error in fallback extraction: {str(e)}")

    return tilbud


def extract_deals_from_pdf(pdf_content: bytes, butik: str) -> tuple:
    """
    Extract deals from PDF content using production scanners.

    Uses the sophisticated scanner implementations with:
    - Font-size based price detection
    - Block-based parsing with column detection
    - Skip patterns to filter non-product text
    - Confidence scoring
    - Duplicate detection

    Args:
        pdf_content: PDF file as bytes
        butik: Store identifier (netto, rema, foetex, bilka, etc.)

    Returns:
        Tuple of (tilbud list, scanner_metadata dict)
    """
    # Check if scanners are available
    if not SCANNERS_AVAILABLE:
        logging.warning("Production scanners not available, using fallback extraction")
        return extract_deals_fallback(pdf_content, butik), {'scanner_version': 'fallback'}

    temp_path = None
    try:
        # Save PDF to temp file (scanners expect file paths)
        with _tempfile.NamedTemporaryFile(suffix='.pdf', delete=False) as f:
            f.write(pdf_content)
            temp_path = f.name

        logging.info(f"Saved PDF to temp file: {temp_path}")

        # Auto-detect store if needed (fallback)
        if not butik:
            butik = _detect_store(pdf_content)
            logging.info(f"Auto-detected store: {butik}")

        # Get the appropriate scanner for this store
        scanner = _get_scanner(butik)
        logging.info(f"Using scanner: {type(scanner).__name__}")

        # Run the scan
        result = scanner.scan(temp_path)

        # Extract deals and metadata
        tilbud = result.get('tilbud', [])
        metadata = {
            'scanner_version': result.get('scanner_version', 'unknown'),
            'antal_sider': result.get('antal_sider', 0),
            'antal_tilbud': len(tilbud),
            'hoj_konfidens': sum(1 for t in tilbud if t.get('konfidens', 0) >= 0.8),
            'kategorier': list(set(t.get('kategori', 'Andet') for t in tilbud)),
            'uge': result.get('uge'),
            'gyldig_fra': result.get('gyldig_fra'),
            'gyldig_til': result.get('gyldig_til')
        }

        logging.info(f"Scan complete: {len(tilbud)} deals, {metadata['hoj_konfidens']} high confidence")

        return tilbud, metadata

    except Exception as e:
        logging.exception(f"Error scanning PDF: {str(e)}")
        return [], {}

    finally:
        # Clean up temp file
        if temp_path and os.path.exists(temp_path):
            try:
                os.unlink(temp_path)
                logging.debug(f"Cleaned up temp file: {temp_path}")
            except Exception as e:
                logging.warning(f"Failed to clean up temp file: {e}")


def upload_to_api(butik: str, gyldig_fra: str, gyldig_til: str, kilde_fil: str, tilbud: list, scan_metadata: dict = None) -> bool:
    """Upload extracted deals to the API with enhanced metadata."""

    if not API_KEY:
        logging.error("DEALS_API_KEY not configured")
        return False

    # Build metadata with scanner information
    meta = {
        "butik": butik,
        "gyldig_fra": gyldig_fra,
        "gyldig_til": gyldig_til,
        "kilde_fil": kilde_fil
    }

    # Add scanner metadata if available
    if scan_metadata:
        meta.update({
            "scanner_version": scan_metadata.get('scanner_version', 'unknown'),
            "antal_sider": scan_metadata.get('antal_sider', 0),
            "uge": scan_metadata.get('uge')
        })

    # Build statistics
    statistik = {
        "antal_tilbud": len(tilbud),
        "hoj_konfidens": sum(1 for t in tilbud if t.get('konfidens', 0) >= 0.8),
        "kategorier": list(set(t.get('kategori', 'Andet') for t in tilbud))
    }

    payload = {
        "meta": meta,
        "statistik": statistik,
        "tilbud": tilbud
    }

    # Debug logging for upload payload
    logging.info(f"Upload payload meta: {meta}")
    logging.info(f"Upload payload statistik: {statistik}")
    logging.info(f"Upload payload tilbud count: {len(tilbud)}")
    if tilbud:
        # Log first 3 tilbud items for debugging
        for i, t in enumerate(tilbud[:3]):
            logging.info(f"Tilbud[{i}]: produkt={t.get('produkt', 'N/A')[:50]}, pris={t.get('total_pris')}, konfidens={t.get('konfidens')}")

    if _requests is None:
        logging.error("requests module not available")
        return False

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
            result = response.json()
            imported_count = result.get('tilbud_imported', 'unknown')
            logging.info(f"API SUCCESS: {imported_count} tilbud imported (sent {len(tilbud)})")
            logging.info(f"API response: {result}")

            # CRITICAL: Check if API actually stored the deals
            if imported_count != len(tilbud):
                logging.warning(f"MISMATCH: Sent {len(tilbud)} but API imported {imported_count}")

            return True
        else:
            logging.error(f"API error: {response.status_code} - {response.text}")
            return False

    except Exception as e:
        logging.exception(f"Error calling API: {str(e)}")
        return False


def move_blob(filename: str, destination: str, metadata: dict = None):
    """Move blob from tilbudsaviser to processed or failed container."""

    if not STORAGE_CONNECTION:
        logging.warning("Storage connection not configured, skipping blob move")
        return

    if _BlobServiceClient is None:
        logging.warning("BlobServiceClient not available, skipping blob move")
        return

    try:
        blob_service = _BlobServiceClient.from_connection_string(STORAGE_CONNECTION)

        # Source blob
        source_container = blob_service.get_container_client("tilbudsaviser")
        source_blob = source_container.get_blob_client(filename)

        # Destination blob with folder structure (year/week/)
        now = _datetime.utcnow()
        dest_path = f"{now.year}/uge{now.isocalendar()[1]:02d}/{filename}"

        dest_container = blob_service.get_container_client(destination)
        dest_blob = dest_container.get_blob_client(dest_path)

        # Copy to destination
        dest_blob.start_copy_from_url(source_blob.url)

        # Set metadata if provided
        if metadata:
            dest_blob.set_blob_metadata(metadata)

        # Delete source
        source_blob.delete_blob()

        logging.info(f"Moved {filename} to {destination}/{dest_path}")

    except Exception as e:
        logging.exception(f"Error moving blob: {str(e)}")
