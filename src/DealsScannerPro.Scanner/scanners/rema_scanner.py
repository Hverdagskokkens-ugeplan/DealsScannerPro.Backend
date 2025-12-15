"""
Scanner V2 - Rema 1000 Tilbudsavis Scanner (Azure Function Version)
===================================================================

Production-quality scanner for Rema 1000 PDF flyers.
Adapted for Azure Functions with logging instead of print statements.
"""

import logging
import json
import re
from pathlib import Path
from typing import List, Dict, Tuple, Optional
from datetime import datetime

import fitz  # PyMuPDF


class RemaScanner:
    """Production scanner for Rema 1000 flyers"""

    # Product categories based on keywords
    CATEGORIES = {
        'Mejeri': ['mælk', 'smør', 'ost', 'yoghurt', 'skyr', 'fløde', 'æg', 'lactofree', 'lurpak', 'arla', 'philadelphia', 'buko', 'cream', 'creme fraiche', 'kærnemælk'],
        'Kød': ['kylling', 'oksekød', 'svinekød', 'flæsk', 'bacon', 'pølse', 'hakket', 'steak', 'mørbrad', 'and', 'lam', 'kalv', 'medister', 'frikadelle', 'kød', 'lever', 'skinke', 'kam', 'nakke', 'kotelet', 'filet'],
        'Fisk': ['laks', 'sild', 'rejer', 'torsk', 'makrel', 'tun', 'fisk', 'fiskefrikadeller', 'rogn', 'krabbe', 'muslinger', 'rødspætte'],
        'Frugt & Grønt': ['æble', 'appelsin', 'banan', 'tomat', 'agurk', 'salat', 'kartoffel', 'gulerod', 'løg', 'kål', 'ananas', 'clementiner', 'granatæble', 'pære', 'citron', 'avocado', 'melon', 'jordbær', 'hindbær', 'blåbær', 'svampe', 'champignon', 'squash', 'peberfrugt', 'broccoli', 'blomkål', 'spinat', 'rucola', 'mango', 'kiwi', 'vindrue'],
        'Brød & Bagværk': ['brød', 'boller', 'rugbrød', 'toast', 'croissant', 'wienerbrød', 'kage', 'æbleskiver', 'bagværk', 'rundstykke', 'flute', 'ciabatta', 'bagel', 'knækbrød'],
        'Drikkevarer': ['cola', 'fanta', 'sprite', 'øl', 'vin', 'juice', 'vand', 'sodavand', 'kakao', 'pepsi', 'carlsberg', 'tuborg', 'heineken', 'royal', 'whisky', 'whiskey', 'vodka', 'gin', 'rom', 'aquavit', 'likør', 'champagne', 'mousserende', 'cider', 'saft', 'smoothie'],
        'Kaffe & Te': ['kaffe', 'te', 'espresso', 'nescafe', 'merrild', 'gevalia', 'senseo', 'dolce gusto', 'nespresso'],
        'Kolonial': ['pasta', 'ris', 'mel', 'sukker', 'olie', 'eddike', 'sauce', 'ketchup', 'sennep', 'mayonnaise', 'remoulade', 'bouillon', 'krydderi', 'salt', 'peber', 'honning', 'nutella', 'peanut', 'müsli', 'havregryn', 'cornflakes'],
        'Snacks': ['chips', 'slik', 'chokolade', 'nødder', 'popcorn', 'kiks', 'småkager', 'flødeboller', 'lakrids', 'vingummi', 'haribo', 'marabou', 'toms'],
        'Frost': ['is', 'frost', 'frossen', 'pizza', 'pommes', 'fritter', 'frosne'],
        'Konserves': ['konserves', 'dåse', 'survare', 'syltede', 'marmelade', 'pickles', 'oliven', 'tomat'],
        'Pålæg': ['pålæg', 'salami', 'leverpostej', 'spegepølse', 'rullepølse', 'hamburgerryg'],
        'Personlig pleje': ['shampoo', 'sæbe', 'tandpasta', 'deodorant', 'creme', 'showergel', 'hårpleje', 'bodylotion', 'barbering'],
        'Rengøring': ['vaskemiddel', 'opvask', 'rengøring', 'affald', 'toiletpapir', 'køkkenrulle', 'skrald', 'poser', 'sæbe'],
        'Non-food': ['tøj', 'sko', 'strømper', 'handsker', 'tæppe', 'legetøj', 'elektronik', 'køkken', 'lampe', 'lys', 'batteri', 'pude', 'dyne', 'sengetøj', 'værktøj'],
    }

    # Skip patterns for non-product lines (Rema 1000 specific)
    SKIP_PATTERNS = [
        r'^pr\.\s*\d',
        r'^\d+[.,]\d+\s*pr\.',
        r'^max\.\s*\d',
        r'^\d+[.,]\d+\s*kr',
        r'^spar\s',
        r'^inkl\.',
        r'^se\s+flere',
        r'^gælder\s+fra',
        r'^gælder\s+(kun\s+)?i\s+uge',
        r'^\d+\s*kg\..*\d',
        r'^liter\s+\d',
        r'^\d+\s*g$',
        r'^forbehold\s+for',
        r'^flere\s+butikker',
        r'^de\s+viste\s+produkt',
        r'^find\s+',
        r'^vind\s+',
        r'^\*',
        r'^læs\s+mere',
        r'^\d+-\d+$',
        r'^tilbuddet\s+gælder',
        r'^vi\s+tager\s+forbehold',
        r'^med\s+forbehold',
        r'^priser.*ændringer',
        r'^se\s+aktuelle',
        r'^kun\s+i\s+denne\s+uge',
        r'^member\s*price',
        r'^medlemspris',
        r'^rema\s*1000',
        r'^god\s+pris',
        r'^ekstra\s+god',
        r'^altid\s+billig',
        r'^ved\s+køb\s+af',
        r'^flere\s+end',
        r'^pr\.\s+kunde',
        r'^er\s+prisen',
        r'^pr\.\s+stk',
        r'^pr\.\s+½',
        r'^op\s+til',
        r'^partivare',
        r'^kl\.\s*[ivx]+',
        r'udenlandske?$',
        r'^\d+\s*[.,]\s*-\s*$',
        r'^\d+\s*,-\s*$',
        r'^\d+\s*\.-\s*$',
        r'^meget\s+mere',
        r'julefrokost',
        r'^[A-ZÆØÅ\s!]+$',
        r'^spar\s+op\s+til',
        r'^fest',
        r'^super\s+tilbud',
        r'^ugens\s+',
        r'^kæmpe\s+',
        r'^\d+\s*stk\.?\s*pr\.',
        r'^ved\s+køb\s+af\s+flere',
        r'^scan\s+qr',
        r'^se\s+opskrift',
        r'^og\s+se\s+opskrift',
        r'^kom\s+fiskefarsen',
        r'^se\s+avisen',
        r'^kasse\s+med',
        r'^\d+[.,]\d{2}$',
        r'^\d+[.,]\d{2}\s*pr\.',
        r'^\d+[.,]\d{2}/\d+[.,]\d{2}',
        r'^\d{1,2}\s+[A-Za-zÆØÅæøå]',
        r'^se\s+åbningstid',
        r'^i\s+din\s+lokale',
        r'^nogle\s+butikker',
        r'^du\s+kan\s+også\s+betale',
        r'^hent\s+scan\s+selv',
        r'^alle\s+ugens\s+dage',
        r'^se\s+mere\s+på',
        r'www\.\w+\.dk',
        r'^mobilepay',
        r'^dankort',
        # Recipe ingredients (bullet points)
        r'^[•·]\s*',
        r'^\u2022',
        # Marketing text
        r'^køb\s+\d+\s+fl',
        r'^køb\s+\d+\s+og',
        r'^blandingsforhold',
        r'^fremgangsmåde',
        # Footer/metadata
        r'^uge\s+\d+/\d+',
        r'hl-repro',
        r'^scan\s+koden',
        r'med\s+scan\s+selv',
        r'^der\s+tages\s+forbehold',
        # App/store info
        r'rema1000\.dk',
        r'^nemt\s+&\s+grønt',
        r'^opskrift',
        # More slogans and marketing
        r'grønnere\s+retter',
        r'discount\s+med\s+holdning',
        r'mens\s+du\s+handler',
        r'enkelt\s+og\s+hurtigt',
        r'indk.bslister',
        r'aviser\s+digitalt',
        r'^det\s+er\s+discount',
        r'^lave\s+grønnere',
        # Cooking instructions
        r'^steges\s+i',
        r'kernetemperatur',
        r'^tilberedningstid',
        r'^\d+\s*°',
        r'^i\s+ca\.\s+\d+\s+min',
        # Magazine/editorial
        r'^vinbladet',
        r'^på\s+årets',
    ]

    # App/member offer patterns
    APP_PATTERNS = [
        r'kun\s+med\s+app',
        r'medlemspris',
        r'member\s*price',
        r'kun\s+for\s+medlemmer',
    ]

    def __init__(self):
        self.unit_patterns = {
            'kg': [r'kg', r'/kg', r'pr\.?\s*kg', r'kilo'],
            'g': [r'gram', r'\bg\b', r'/g'],
            'L': [r'liter', r'\bL\b', r'/L', r'\bl\b'],
            'ml': [r'ml', r'/ml'],
            'cl': [r'cl', r'/cl'],
            'stk': [r'stk\.?', r'styk', r'pr\.?\s*stk', r'/stk'],
            'pk': [r'pakke', r'pk', r'/pk', r'pak'],
        }
        self._seen_products = {}

    def _clean_text(self, text: str) -> str:
        """Clean text from control characters"""
        if not text:
            return ""
        text = re.sub(r'[\x00-\x1f\x7f-\x9f]', '', text)
        text = re.sub(r'\s+', ' ', text)
        return text.strip()

    def _clean_product_name(self, name: str) -> str:
        """Clean product name from prices and unwanted text"""
        if not name:
            return ""

        name = re.sub(r'^\d+\s*stk\.?\s*pr\.\s*(variant|kunde)\s*', '', name, flags=re.IGNORECASE)
        name = re.sub(r'^\d+\s*stk\.?\s*pr\.\s*(variant|kunde)\s*[\d.,]+\s*', '', name, flags=re.IGNORECASE)
        name = re.sub(r'^ved\s+køb\s+af\s+flere\s+end\s+\d+\s*stk.*?(?:er\s+prisen\s*)?', '', name, flags=re.IGNORECASE)
        name = re.sub(r'^\d{1,2}\s+(?=[A-ZÆØÅa-zæøå])', '', name)
        name = re.sub(r'^og\s+se\s+opskrift.*$', '', name, flags=re.IGNORECASE)
        name = re.sub(r'^\d+\s*[.,]\s*-\s*', '', name)
        name = re.sub(r'^\d+\s*,-\s*', '', name)
        name = re.sub(r'^\d+[.,]\d{2}\s+', '', name)
        name = re.sub(r'\s+\d+\.\s*$', '', name)
        name = re.sub(r'\s+\d+\.-\s*$', '', name)
        name = re.sub(r'\s+\d+,-\s*$', '', name)
        name = re.sub(r'\s+\d+,\d+\s*$', '', name)
        name = re.sub(r'\s+\d+$', '', name)
        name = re.sub(r'\s+\d+\s*[.,]\s*-\s*$', '', name)
        name = re.sub(r'\s*kr\.?\s*$', '', name)
        name = re.sub(r'\s*,-\s*$', '', name)
        name = re.sub(r'\s*\.-\s*$', '', name)

        return name.strip()

    def _is_skip_line(self, text: str) -> bool:
        """Check if line should be skipped"""
        text_lower = text.lower().strip()
        for pattern in self.SKIP_PATTERNS:
            if re.search(pattern, text_lower):
                return True
        return False

    def _is_app_offer(self, text: str) -> bool:
        """Check if this is an app/member offer"""
        text_lower = text.lower()
        for pattern in self.APP_PATTERNS:
            if re.search(pattern, text_lower):
                return True
        return False

    def _is_valid_product(self, offer: Dict) -> bool:
        """Check if an offer is a valid product"""
        produkt = offer.get('produkt', '')
        konfidens = offer.get('konfidens', 0)

        if konfidens < 0.5 and not offer.get('total_pris'):
            return False
        if len(produkt) < 3:
            return False
        if re.match(r'^[\d\s\-]+$', produkt):
            return False
        if re.match(r'^\d+-pak$', produkt.lower()):
            return False
        if re.match(r'^\d+\s*[.,]\s*-\s*$', produkt):
            return False
        if re.match(r'^[A-ZÆØÅ\s!]+$', produkt) and len(produkt) > 5:
            return False
        if re.search(r'\d+\s*[.,]\s*-', produkt) and len(produkt) < 10:
            return False

        skip_starts = [
            'gælder', 'forbehold', 'flere butikker',
            'de viste', 'find', 'vind',
            'baseret på', 'læs mere', 'rema 1000',
            'tilbud', 'member', 'medlems',
            'meget mere', 'julefrokost', 'fest', 'super',
            'åbningstid', 'du kan også', 'hent scan',
            'mobilepay', 'dankort', 'se mere'
        ]
        for skip in skip_starts:
            if produkt.lower().startswith(skip):
                return False

        marketing_keywords = ['julefrokost', 'meget mere', 'super tilbud', 'kæmpe tilbud']
        for keyword in marketing_keywords:
            if keyword in produkt.lower():
                return False

        if re.match(r'^\d+[.,]\d{2}$', produkt):
            return False
        if re.match(r'^scan\s+qr', produkt.lower()):
            return False
        if len(produkt) < 5 and not offer.get('total_pris'):
            return False

        if not offer.get('total_pris'):
            problematic_starts = [
                'og se', 'se avisen', 'kasse med', '& ', 'melblanding',
                'glutenfri julecookie'
            ]
            for start in problematic_starts:
                if produkt.lower().startswith(start):
                    return False

        if re.match(r'^[&]\s', produkt):
            return False

        return True

    def _categorize_product(self, product_name: str) -> str:
        """Categorize product based on name"""
        name_lower = product_name.lower()

        priority_order = [
            'Fisk', 'Kød', 'Mejeri', 'Frugt & Grønt',
            'Pålæg', 'Brød & Bagværk', 'Kaffe & Te',
            'Drikkevarer', 'Snacks', 'Frost', 'Konserves',
            'Kolonial', 'Personlig pleje', 'Rengøring', 'Non-food'
        ]

        for category in priority_order:
            if category in self.CATEGORIES:
                for keyword in self.CATEGORIES[category]:
                    if keyword in name_lower:
                        return category

        return "Andet"

    def _normalize_quantity(self, maengde: str, enhed: str) -> Dict:
        """Normalize quantity to standard format"""
        if not maengde:
            return {"original": None, "normalized": None, "value": None, "unit": enhed}

        original = maengde
        match = re.search(r'([\d.,]+)\s*-?\s*([\d.,]*)?\s*(g|kg|ml|l|cl|stk|pak)?', maengde.lower())

        if match:
            value_str = match.group(1).replace(',', '.')
            try:
                value = float(value_str)
            except:
                value = None

            unit = match.group(3) or enhed
            normalized_value = value
            normalized_unit = unit

            if unit == 'kg' and value:
                normalized_value = value * 1000
                normalized_unit = 'g'
            elif unit == 'l' and value:
                normalized_value = value * 1000
                normalized_unit = 'ml'
            elif unit == 'cl' and value:
                normalized_value = value * 10
                normalized_unit = 'ml'

            return {
                "original": original,
                "value": value,
                "unit": unit,
                "normalized_value": normalized_value,
                "normalized_unit": normalized_unit
            }

        return {"original": original, "normalized": None, "value": None, "unit": enhed}

    def _calculate_unit_price(self, total_pris: float, maengde_info: Dict) -> Optional[float]:
        """Calculate unit price if missing"""
        if not total_pris or not maengde_info.get('normalized_value'):
            return None

        value = maengde_info['normalized_value']
        unit = maengde_info.get('normalized_unit', '')

        if unit == 'g' and value > 0:
            return round(total_pris / value * 1000, 2)
        elif unit == 'ml' and value > 0:
            return round(total_pris / value * 1000, 2)
        elif unit in ['stk', 'pak'] and value > 0:
            return round(total_pris / value, 2)

        return None

    def _calculate_confidence(self, offer: Dict) -> float:
        """Calculate confidence score (0-1)"""
        score = 1.0
        produkt = offer.get('produkt', '')

        if not offer.get('total_pris'):
            if len(produkt) >= 10 and offer.get('kategori') != 'Andet':
                score -= 0.2
            else:
                score -= 0.3

        if not produkt or len(produkt) < 3:
            score -= 0.3
        if not offer.get('maengde'):
            score -= 0.05
        if not offer.get('pris_per_enhed'):
            score -= 0.05

        if re.search(r'^\d+\s*(g|kg|ml|l|cl|stk)', produkt.lower()):
            score -= 0.3
        if len(produkt) < 5:
            score -= 0.2

        return max(0, min(1, round(score, 2)))

    def _check_duplicate(self, produkt: str, pris: float) -> Dict:
        """Check for duplicates"""
        key = f"{produkt.lower().strip()}_{pris}"

        if key in self._seen_products:
            self._seen_products[key]['count'] += 1
            return {
                "is_duplicate": True,
                "first_seen_page": self._seen_products[key]['first_page'],
                "occurrence": self._seen_products[key]['count']
            }
        else:
            self._seen_products[key] = {'count': 1, 'first_page': None}
            return {"is_duplicate": False}

    def _extract_validity_period(self, doc) -> Optional[Dict]:
        """Extract validity period from PDF"""
        text = ''
        for i in range(min(5, len(doc))):
            text += doc[i].get_text() + ' '
        text_lower = text.lower()

        months = {'januar': 1, 'februar': 2, 'marts': 3, 'april': 4, 'maj': 5, 'juni': 6,
                  'juli': 7, 'august': 8, 'september': 9, 'oktober': 10, 'november': 11, 'december': 12,
                  'jan': 1, 'feb': 2, 'mar': 3, 'apr': 4, 'jun': 6, 'jul': 7, 'aug': 8,
                  'sep': 9, 'okt': 10, 'nov': 11, 'dec': 12}

        pattern1 = r'gælder\s+fra\s+\w+\s+(?:den\s+)?(\d{1,2})\.\s*(\w+)\s+til\s+og\s+med\s+\w+\s+(?:den\s+)?(\d{1,2})\.\s*(\w+)\s*(\d{4})?'
        match1 = re.search(pattern1, text_lower)
        if match1:
            d1, m1, d2, m2, year = match1.groups()
            year = int(year) if year else datetime.now().year
            m1_num = months.get(m1, 12)
            m2_num = months.get(m2, 12)
            return {
                "fra": f'{year}-{m1_num:02d}-{int(d1):02d}',
                "til": f'{year}-{m2_num:02d}-{int(d2):02d}'
            }

        pattern2 = r'fra\s+\w+\s+(\d{1,2})\.\s*(\w+)\.?\s+til.*?(\d{1,2})\.\s*(\w+)'
        match2 = re.search(pattern2, text_lower)
        if match2:
            d1, m1, d2, m2 = match2.groups()
            year = datetime.now().year
            m1_num = months.get(m1[:3], 12)
            m2_num = months.get(m2[:3], 12)
            return {
                "fra": f'{year}-{m1_num:02d}-{int(d1):02d}',
                "til": f'{year}-{m2_num:02d}-{int(d2):02d}'
            }

        uge_match = re.search(r'uge\s*(\d{1,2})', text_lower)
        if uge_match:
            from datetime import timedelta
            week = int(uge_match.group(1))
            year = datetime.now().year
            jan1 = datetime(year, 1, 1)
            if jan1.weekday() <= 3:
                first_monday = jan1 - timedelta(days=jan1.weekday())
            else:
                first_monday = jan1 + timedelta(days=7-jan1.weekday())
            start = first_monday + timedelta(weeks=week-1)
            end = start + timedelta(days=6)
            return {
                "fra": start.strftime('%Y-%m-%d'),
                "til": end.strftime('%Y-%m-%d'),
                "uge": week
            }

        return None

    def _merge_product_name(self, lines: List[str], start_idx: int, end_idx: int) -> str:
        """Merge product name from multiple lines"""
        name_parts = []

        for i in range(start_idx, min(end_idx + 1, len(lines))):
            line = lines[i]
            if isinstance(line, dict):
                text = line.get('text', '')
            else:
                text = line

            text = self._clean_text(text)

            if self._is_skip_line(text):
                continue
            if re.match(r'^pr\.\s', text.lower()):
                continue
            if re.match(r'^\d+[-–]\d+\s*(g|kg|ml|l)', text.lower()):
                continue
            if re.match(r'^\d+\s*(g|kg|ml|l|cl|stk)\.?$', text.lower()):
                continue
            if re.match(r'^\d+\s*[.,]\s*-\s*$', text):
                continue
            if re.match(r'^[A-ZÆØÅ\s!]+$', text) and len(text) > 5:
                continue

            if text and len(text) > 1:
                name_parts.append(text)
                if len(name_parts) >= 4:
                    break

        full_name = ' '.join(name_parts)
        full_name = re.sub(r'\s+', ' ', full_name)
        full_name = re.sub(r'\s*-\s*$', '', full_name)

        return full_name.strip()

    def _parse_variants(self, text: str) -> Tuple[str, List[str]]:
        """Parse variants from text"""
        variants = []
        main_product = text

        eller_match = re.search(r'^(.+?)\s+eller\s+(.+)$', text, re.IGNORECASE)
        if eller_match:
            main_product = eller_match.group(1).strip()
            variant_text = eller_match.group(2).strip()

            if ',' in variant_text:
                variants = [v.strip() for v in variant_text.split(',')]
            else:
                variants = [variant_text]

        elif '/' in text and not re.search(r'\d/\d', text):
            parts = text.split('/')
            if len(parts) == 2 and len(parts[0]) > 3 and len(parts[1]) > 2:
                main_product = parts[0].strip()
                variants = [parts[1].strip()]

        return main_product, variants

    def _extract_text_with_prices(self, page) -> Tuple[List[Dict], List[Dict]]:
        """Extract text and find prices based on Rema 1000 format"""
        blocks = page.get_text("dict")["blocks"]

        lines = []
        font_prices = []

        line_index = 0

        for block in blocks:
            if "lines" not in block:
                continue

            for line in block["lines"]:
                spans = line["spans"]
                line_text = ""
                line_x = line["bbox"][0] if "bbox" in line else 0

                full_line = "".join(s["text"] for s in spans).strip().lower()
                is_skip = self._is_skip_line(full_line) or self._is_app_offer(full_line)

                for span in spans:
                    text = span["text"]
                    font_size = span["size"]
                    line_text += text

                    text_stripped = text.strip()

                    if is_skip:
                        continue

                    # Rema 1000 format: Large prices with ".-" or ",-"
                    if font_size >= 50:
                        price_match = re.search(r'(\d+)\s*[.,]\s*-', text_stripped)
                        if price_match:
                            font_prices.append({
                                'value': float(price_match.group(1)),
                                'line_index': line_index,
                                'x': line_x
                            })
                        elif re.match(r'^\d+[.,]\d{2}$', text_stripped):
                            decimal_match = re.match(r'^(\d+)[.,](\d{2})$', text_stripped)
                            if decimal_match:
                                font_prices.append({
                                    'value': float(f"{decimal_match.group(1)}.{decimal_match.group(2)}"),
                                    'line_index': line_index,
                                    'x': line_x
                                })
                        else:
                            combo_match = re.search(r'(\d+)\s*[.,]\s*-\s*$', text_stripped)
                            if combo_match:
                                font_prices.append({
                                    'value': float(combo_match.group(1)),
                                    'line_index': line_index,
                                    'x': line_x
                                })

                line_stripped = self._clean_text(line_text)
                if line_stripped:
                    lines.append({'text': line_stripped, 'x': line_x})
                    line_index += 1

        return lines, font_prices

    def _find_product_blocks(self, lines: List[Dict], line_to_page: Dict, font_prices: List[Dict]) -> List[Dict]:
        """Find product blocks"""
        blocks = []
        current_block_start = None
        current_block_x = None

        font_price_lines = {fp['line_index']: fp for fp in font_prices}

        def get_text(line):
            return line.get('text', '') if isinstance(line, dict) else line

        def get_x(line):
            return line.get('x', 0) if isinstance(line, dict) else 0

        def is_same_column(x1, x2, tolerance=50):
            return abs(x1 - x2) < tolerance

        for i, line in enumerate(lines):
            text = get_text(line)
            line_x = get_x(line)

            if not text or self._is_skip_line(text):
                continue

            start_new = False

            if current_block_start is None:
                start_new = True
            elif not is_same_column(line_x, current_block_x):
                start_new = True

            if i > 0 and (i - 1) in font_price_lines:
                start_new = True

            if start_new and current_block_start is not None:
                blocks.append({
                    'start_idx': current_block_start,
                    'end_idx': i - 1,
                    'lines': lines[current_block_start:i],
                    'page': line_to_page.get(current_block_start, 1),
                    'x': current_block_x
                })

            if start_new:
                current_block_start = i
                current_block_x = line_x

        if current_block_start is not None:
            blocks.append({
                'start_idx': current_block_start,
                'end_idx': len(lines) - 1,
                'lines': lines[current_block_start:],
                'page': line_to_page.get(current_block_start, 1),
                'x': current_block_x
            })

        return blocks

    def _block_to_offer(self, block: Dict, all_lines: List, font_prices: List[Dict]) -> Optional[Dict]:
        """Convert block to offer - Rema 1000 format"""
        lines = block['lines']
        if not lines:
            return None

        produkt = self._merge_product_name(
            all_lines,
            block['start_idx'],
            min(block['end_idx'], block['start_idx'] + 5)
        )

        if not produkt or len(produkt) < 2:
            return None

        produkt = self._clean_product_name(produkt)

        if not produkt or len(produkt) < 2:
            return None

        produkt, varianter = self._parse_variants(produkt)
        varianter = [self._clean_product_name(v) for v in varianter if self._clean_product_name(v)]

        if self._is_app_offer(produkt):
            return None

        # Find price from font_prices
        total_pris = None
        block_start = block['start_idx']
        block_end = block['end_idx']

        for fp in font_prices:
            if block_start <= fp['line_index'] <= block_end + 2:
                total_pris = fp['value']
                break

        # Rema format: Price can also be in text as "XX.-" or "XX.XX"
        if not total_pris:
            for line in lines:
                text = line.get('text', '') if isinstance(line, dict) else line
                price_match = re.search(r'(\d+)\s*[.,]\s*-', text)
                if price_match:
                    total_pris = float(price_match.group(1))
                    break
                decimal_match = re.match(r'^(\d+)[.,](\d{2})\s', text)
                if decimal_match:
                    total_pris = float(f"{decimal_match.group(1)}.{decimal_match.group(2)}")
                    break

        maengde = None
        pris_per_enhed = None
        enhed = "kr"
        kommentar = None

        for line in lines:
            text = line.get('text', '') if isinstance(line, dict) else line
            text_lower = text.lower()

            m = re.search(r'(\d+[-–]?\d*)\s*(g|kg|ml|l|cl|stk)', text_lower)
            if m and not maengde:
                maengde = m.group(0)
                enhed = m.group(2).upper() if m.group(2) in ['l', 'L'] else m.group(2)

            pr_match = re.search(r'([\d.,]+)\s*pr\.?\s*(kg|l|liter|stk|½\s*kg)', text_lower)
            if pr_match:
                try:
                    pris_per_enhed = float(pr_match.group(1).replace(',', '.'))
                    unit = pr_match.group(2)
                    if '½' in unit or 'halv' in unit.lower():
                        enhed = '½ kg'
                    elif 'kg' in unit:
                        enhed = 'kg'
                    elif 'l' in unit:
                        enhed = 'L'
                    else:
                        enhed = 'stk'
                except:
                    pass

            max_match = re.search(r'(max\.?\s*[\d.,]+\s*pr\.?\s*\w+)', text_lower)
            if max_match:
                kommentar = max_match.group(1).capitalize()

            if 'partivare' in text_lower:
                if not kommentar:
                    kommentar = "Partivare"

        maengde_info = self._normalize_quantity(maengde, enhed)

        if not pris_per_enhed and total_pris:
            pris_per_enhed = self._calculate_unit_price(total_pris, maengde_info)

        dup_info = self._check_duplicate(produkt, total_pris or 0)
        if dup_info['is_duplicate']:
            dup_info['first_seen_page'] = block.get('page', 1)
        else:
            self._seen_products[f"{produkt.lower().strip()}_{total_pris or 0}"]['first_page'] = block.get('page', 1)

        kategori = self._categorize_product(produkt)

        offer = {
            "produkt": produkt,
            "total_pris": total_pris,
            "pris_per_enhed": pris_per_enhed,
            "enhed": enhed,
            "maengde": maengde_info.get('original'),
            "maengde_normaliseret": {
                "value": maengde_info.get('normalized_value'),
                "unit": maengde_info.get('normalized_unit')
            } if maengde_info.get('normalized_value') else None,
            "kategori": kategori,
            "kommentar": kommentar,
            "side": block.get('page', 1),
        }

        if varianter:
            offer["varianter"] = varianter

        if dup_info['is_duplicate']:
            offer["er_duplikat"] = True
            offer["første_side"] = dup_info['first_seen_page']

        offer["konfidens"] = self._calculate_confidence(offer)
        offer["needs_review"] = offer["konfidens"] < 0.7

        return offer

    def scan(self, pdf_path: str, pages: str = None) -> Dict:
        """
        Scan PDF and return structured data.

        Args:
            pdf_path: Path to PDF file
            pages: Page range (e.g., "1-10") or None for all pages

        Returns:
            Dictionary with meta, statistik, and tilbud
        """
        filepath = Path(pdf_path)

        if not filepath.exists():
            return {"error": f"File not found: {pdf_path}"}

        doc = fitz.open(str(filepath))
        total_pages = len(doc)

        if pages:
            if '-' in pages:
                start, end = map(int, pages.split('-'))
                selected_pages = list(range(start, min(end + 1, total_pages + 1)))
            else:
                selected_pages = [int(p) for p in pages.split(',')]
        else:
            selected_pages = list(range(1, total_pages + 1))

        logging.info(f"Rema 1000 Scanner: {filepath.name}, Pages: {len(selected_pages)} of {total_pages}")

        self._seen_products = {}
        all_lines = []
        all_font_prices = []
        line_to_page = {}

        validity_period = self._extract_validity_period(doc)

        for page_num in selected_pages:
            page = doc[page_num - 1]
            lines, font_prices = self._extract_text_with_prices(page)

            if lines:
                start_idx = len(all_lines)
                all_lines.extend(lines)

                for fp in font_prices:
                    fp['line_index'] += start_idx
                all_font_prices.extend(font_prices)

                for i in range(len(lines)):
                    line_to_page[start_idx + i] = page_num

        doc.close()

        blocks = self._find_product_blocks(all_lines, line_to_page, all_font_prices)

        tilbud_raw = []
        for block in blocks:
            offer = self._block_to_offer(block, all_lines, all_font_prices)
            if offer:
                tilbud_raw.append(offer)

        tilbud = [t for t in tilbud_raw if self._is_valid_product(t)]

        high_confidence = len([t for t in tilbud if t.get('konfidens', 0) >= 0.7])
        needs_review = len([t for t in tilbud if t.get('needs_review')])
        duplicates = len([t for t in tilbud if t.get('er_duplikat')])

        kategori_count = {}
        for t in tilbud:
            kat = t.get('kategori', 'Andet')
            kategori_count[kat] = kategori_count.get(kat, 0) + 1

        logging.info(f"Found {len(tilbud)} offers, {high_confidence} high confidence, {needs_review} need review")

        return {
            "meta": {
                "kilde_fil": filepath.name,
                "butik": "Rema 1000",
                "gyldig_fra": validity_period.get('fra') if validity_period else None,
                "gyldig_til": validity_period.get('til') if validity_period else None,
                "uge": validity_period.get('uge') if validity_period else None,
                "scannet_tidspunkt": datetime.now().isoformat(),
                "scanner_version": "1.0",
                "antal_sider": total_pages,
                "sider_scannet": pages if pages else "alle"
            },
            "statistik": {
                "antal_tilbud": len(tilbud),
                "høj_konfidens": high_confidence,
                "skal_gennemses": needs_review,
                "duplikater": duplicates,
                "kategorier": kategori_count
            },
            "tilbud": tilbud
        }
