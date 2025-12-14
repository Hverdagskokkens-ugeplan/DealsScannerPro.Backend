"""
DealsScannerPro - Blob Triggered Scanner Function
==================================================

Automatically processes PDF flyers when uploaded to blob storage.

Flow:
1. PDF uploaded to 'tilbudsaviser' container
2. This function triggers and downloads the PDF
3. Scanner extracts deals from PDF
4. Results are POSTed to the API
5. PDF is moved to 'processed' or 'failed' container
"""

import azure.functions as func
import logging
import json
import re
import os
import requests
from datetime import datetime, timedelta
from io import BytesIO
from azure.storage.blob import BlobServiceClient

app = func.FunctionApp()

# Configuration
API_BASE_URL = os.environ.get("DEALS_API_URL", "https://func-dealscanner-prod.azurewebsites.net")
API_KEY = os.environ.get("DEALS_API_KEY", "")
STORAGE_CONNECTION = os.environ.get("AzureWebJobsStorage", "")


@app.blob_trigger(
    arg_name="blob",
    path="tilbudsaviser/{name}",
    connection="AzureWebJobsStorage"
)
def process_tilbudsavis(blob: func.InputStream):
    """
    Process uploaded PDF flyer.

    Expected filename format: {butik}_{year}-uge{week}.pdf
    Examples: netto_2025-uge51.pdf, rema_2025-uge51.pdf
    """
    filename = blob.name.split('/')[-1]  # Get just the filename
    logging.info(f"Processing PDF: {filename}, Size: {blob.length} bytes")

    try:
        # Parse filename to extract store and week
        butik, gyldig_fra, gyldig_til = parse_filename(filename)
        logging.info(f"Parsed: butik={butik}, fra={gyldig_fra}, til={gyldig_til}")

        # Read PDF content
        pdf_content = blob.read()
        logging.info(f"Read {len(pdf_content)} bytes from blob")

        # Process PDF and extract deals
        tilbud = extract_deals_from_pdf(pdf_content, butik)
        logging.info(f"Extracted {len(tilbud)} deals from PDF")

        if not tilbud:
            logging.warning(f"No deals extracted from {filename}")
            move_blob(filename, "failed", {"error": "No deals extracted"})
            return

        # Upload to API
        upload_result = upload_to_api(butik, gyldig_fra, gyldig_til, filename, tilbud)

        if upload_result:
            logging.info(f"Successfully uploaded {len(tilbud)} deals for {butik}")
            move_blob(filename, "processed", {
                "processedAt": datetime.utcnow().isoformat(),
                "dealsExtracted": str(len(tilbud))
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
    jan4 = datetime(year, 1, 4)
    week_start = jan4 - timedelta(days=jan4.weekday())  # Monday of week 1
    gyldig_fra = week_start + timedelta(weeks=week - 1)
    gyldig_til = gyldig_fra + timedelta(days=6)

    return butik, gyldig_fra.strftime('%Y-%m-%d'), gyldig_til.strftime('%Y-%m-%d')


def extract_deals_from_pdf(pdf_content: bytes, butik: str) -> list:
    """
    Extract deals from PDF content using PyMuPDF.

    This is a simplified extraction - the full scanner logic from
    DealsScannerPro can be integrated here for better results.
    """
    try:
        import fitz  # PyMuPDF
    except ImportError:
        logging.error("PyMuPDF not installed")
        return []

    tilbud = []

    try:
        # Open PDF from bytes
        doc = fitz.open(stream=pdf_content, filetype="pdf")
        logging.info(f"PDF has {len(doc)} pages")

        for page_num in range(len(doc)):
            page = doc[page_num]
            text = page.get_text()

            # Extract deals from page text
            # This is a simplified version - integrate full scanner for production
            page_deals = extract_deals_from_text(text, page_num + 1, butik)
            tilbud.extend(page_deals)

        doc.close()

    except Exception as e:
        logging.exception(f"Error reading PDF: {str(e)}")

    return tilbud


def extract_deals_from_text(text: str, page_num: int, butik: str) -> list:
    """
    Simple deal extraction from text.

    This is a placeholder - integrate the full netto_scanner.py / rema_scanner.py
    logic here for production use.
    """
    tilbud = []

    # Simple price pattern matching
    # Pattern: product name followed by price like "49,95" or "49.-"
    lines = text.split('\n')

    current_product = None

    for line in lines:
        line = line.strip()
        if not line:
            continue

        # Skip common non-product lines
        skip_patterns = [
            r'^pr\.\s*\d', r'^max\.\s*\d', r'^spar\s', r'^inkl\.',
            r'^gælder', r'^forbehold', r'^\d+-\d+$', r'^www\.',
            r'^netto', r'^rema', r'^tilbudsavis', r'^uge\s*\d+'
        ]

        if any(re.match(p, line.lower()) for p in skip_patterns):
            continue

        # Look for price patterns
        price_match = re.search(r'(\d+)[,.](\d{2})(?:\s*kr)?\.?$|(\d+)\.-$', line)

        if price_match:
            if price_match.group(3):  # Pattern like "49.-"
                price = float(price_match.group(3))
            else:
                price = float(f"{price_match.group(1)}.{price_match.group(2)}")

            # Use previous line as product name if current line is just a price
            product_text = line[:price_match.start()].strip()
            if not product_text and current_product:
                product_text = current_product

            if product_text and len(product_text) > 2 and price > 0:
                tilbud.append({
                    "produkt": product_text,
                    "total_pris": price,
                    "pris_per_enhed": price,
                    "enhed": "stk",
                    "maengde": "1 stk",
                    "kategori": categorize_product(product_text),
                    "konfidens": 0.7,  # Lower confidence for simple extraction
                    "side": page_num
                })
        else:
            # Remember this line as potential product name
            if len(line) > 3 and not line.isdigit():
                current_product = line

    return tilbud


def categorize_product(product: str) -> str:
    """Categorize product based on keywords."""
    product_lower = product.lower()

    categories = {
        'Mejeri': ['mælk', 'smør', 'ost', 'yoghurt', 'skyr', 'fløde', 'æg'],
        'Kød': ['kylling', 'oksekød', 'svinekød', 'flæsk', 'bacon', 'pølse', 'hakket', 'kød'],
        'Fisk': ['laks', 'sild', 'rejer', 'torsk', 'fisk'],
        'Frugt & Grønt': ['æble', 'appelsin', 'banan', 'tomat', 'agurk', 'salat', 'kartoffel'],
        'Brød & Bagværk': ['brød', 'boller', 'rugbrød', 'kage'],
        'Drikkevarer': ['cola', 'øl', 'vin', 'juice', 'vand', 'sodavand'],
    }

    for category, keywords in categories.items():
        if any(kw in product_lower for kw in keywords):
            return category

    return "Andet"


def upload_to_api(butik: str, gyldig_fra: str, gyldig_til: str, kilde_fil: str, tilbud: list) -> bool:
    """Upload extracted deals to the API."""

    if not API_KEY:
        logging.error("DEALS_API_KEY not configured")
        return False

    payload = {
        "meta": {
            "butik": butik,
            "gyldig_fra": gyldig_fra,
            "gyldig_til": gyldig_til,
            "kilde_fil": kilde_fil
        },
        "tilbud": tilbud
    }

    try:
        response = requests.post(
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
            logging.info(f"API response: {result}")
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

    try:
        blob_service = BlobServiceClient.from_connection_string(STORAGE_CONNECTION)

        # Source blob
        source_container = blob_service.get_container_client("tilbudsaviser")
        source_blob = source_container.get_blob_client(filename)

        # Destination blob with folder structure (year/week/)
        now = datetime.utcnow()
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
