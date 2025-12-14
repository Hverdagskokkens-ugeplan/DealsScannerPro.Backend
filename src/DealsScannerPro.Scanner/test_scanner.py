#!/usr/bin/env python3
"""
Local Scanner Test Script
=========================

Test the PDF scanners locally without deploying to Azure.

Usage:
    python test_scanner.py <pdf_file> [--store netto|rema] [--json output.json]

Examples:
    python test_scanner.py tilbud.pdf
    python test_scanner.py rema_uge51.pdf --store rema
    python test_scanner.py netto.pdf --json results.json
"""

import argparse
import json
import sys
from pathlib import Path

# Import the same scanner modules used by the Azure Function
from scanners import get_scanner, detect_store


def main():
    parser = argparse.ArgumentParser(
        description="Test PDF scanners locally",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__
    )
    parser.add_argument("pdf", help="Path to PDF file to scan")
    parser.add_argument("--store", "-s", choices=["netto", "rema", "foetex", "bilka"],
                        help="Store type (auto-detected if not specified)")
    parser.add_argument("--json", "-j", metavar="FILE", help="Save results to JSON file")
    parser.add_argument("--pages", "-p", help="Page range to scan (e.g., '1-5' or '1,3,5')")
    parser.add_argument("--verbose", "-v", action="store_true", help="Show detailed output")

    args = parser.parse_args()

    # Validate PDF exists
    pdf_path = Path(args.pdf)
    if not pdf_path.exists():
        print(f"Error: File not found: {pdf_path}")
        sys.exit(1)

    if not pdf_path.suffix.lower() == ".pdf":
        print(f"Warning: File does not have .pdf extension: {pdf_path}")

    # Detect or use specified store
    if args.store:
        store = args.store
        print(f"Using specified store: {store}")
    else:
        print("Auto-detecting store...")
        with open(pdf_path, "rb") as f:
            pdf_content = f.read()
        store = detect_store(pdf_content)
        print(f"Detected store: {store}")

    # Get scanner and run
    print(f"\nScanning: {pdf_path.name}")
    print("-" * 50)

    scanner = get_scanner(store)
    print(f"Scanner: {type(scanner).__name__}")

    result = scanner.scan(str(pdf_path), pages=args.pages)

    # Check for errors
    if "error" in result:
        print(f"Error: {result['error']}")
        sys.exit(1)

    # Extract data
    meta = result.get("meta", {})
    statistik = result.get("statistik", {})
    tilbud = result.get("tilbud", [])

    # Print summary
    print(f"\n{'='*50}")
    print("SCAN RESULTS")
    print(f"{'='*50}")
    print(f"Store:           {meta.get('butik', 'Unknown')}")
    print(f"Pages scanned:   {meta.get('antal_sider', 'Unknown')}")
    print(f"Validity:        {meta.get('gyldig_fra', '?')} - {meta.get('gyldig_til', '?')}")
    print(f"Scanner version: {meta.get('scanner_version', 'Unknown')}")
    print()
    print(f"Total deals:     {len(tilbud)}")
    print(f"High confidence: {statistik.get('høj_konfidens', 0)} ({_percent(statistik.get('høj_konfidens', 0), len(tilbud))})")
    print(f"Needs review:    {statistik.get('skal_gennemses', 0)}")
    print(f"Duplicates:      {statistik.get('duplikater', 0)}")

    # Categories
    kategorier = statistik.get("kategorier", {})
    if kategorier:
        print(f"\nCategories:")
        for kat, count in sorted(kategorier.items(), key=lambda x: -x[1]):
            print(f"  {kat}: {count}")

    # Print deals
    print(f"\n{'='*50}")
    print("DEALS")
    print(f"{'='*50}")

    for i, t in enumerate(tilbud, 1):
        produkt = t.get("produkt", "Unknown")[:60]
        pris = t.get("total_pris")
        konf = t.get("konfidens", 0)
        side = t.get("side", "?")
        kategori = t.get("kategori", "")

        # Format price
        pris_str = f"{pris:.2f} kr" if pris else "N/A"

        # Confidence indicator
        if konf >= 0.8:
            konf_indicator = "***"
        elif konf >= 0.6:
            konf_indicator = "** "
        else:
            konf_indicator = "*  "

        # Flags
        flags = []
        if t.get("er_duplikat"):
            flags.append("DUP")
        if t.get("needs_review"):
            flags.append("REV")
        flags_str = f" [{','.join(flags)}]" if flags else ""

        print(f"{i:3}. {konf_indicator} {produkt:<60} {pris_str:>10}  (s.{side}){flags_str}")

        if args.verbose:
            if t.get("maengde"):
                print(f"         Mængde: {t['maengde']}")
            if t.get("pris_per_enhed"):
                print(f"         Pr. enhed: {t['pris_per_enhed']} {t.get('enhed', '')}")
            if t.get("varianter"):
                print(f"         Varianter: {', '.join(t['varianter'])}")
            if t.get("kommentar"):
                print(f"         Kommentar: {t['kommentar']}")
            print()

    # Confidence legend
    print(f"\n*** = High confidence (>=0.8)")
    print(f"**  = Medium confidence (>=0.6)")
    print(f"*   = Low confidence (<0.6)")

    # Save to JSON if requested
    if args.json:
        output_path = Path(args.json)
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(result, f, ensure_ascii=False, indent=2)
        print(f"\nResults saved to: {output_path}")

    print(f"\nDone!")


def _percent(count, total):
    if total == 0:
        return "0%"
    return f"{count/total*100:.0f}%"


if __name__ == "__main__":
    main()
