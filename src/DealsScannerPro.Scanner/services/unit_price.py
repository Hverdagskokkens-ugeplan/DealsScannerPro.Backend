"""
Unit Price Calculator
=====================

Deterministic calculation of unit prices (kr/L, kr/kg, kr/stk)
from offer data. Matches the C# implementation in the API.
"""

from typing import Optional, Tuple
from dataclasses import dataclass


@dataclass
class UnitPrice:
    """Calculated unit price."""
    value: float
    unit: str  # kr/L, kr/kg, kr/stk

    def __str__(self):
        return f"{self.value:.2f} {self.unit}"


def calculate_unit_price(
    price_value: Optional[float],
    deposit_value: Optional[float],
    net_amount_value: Optional[float],
    net_amount_unit: Optional[str],
    pack_count: Optional[int] = None
) -> Optional[UnitPrice]:
    """
    Calculate unit price from offer data.

    Args:
        price_value: Total price in kr
        deposit_value: Deposit (pant) in kr
        net_amount_value: Amount value (e.g., 500 for 500g)
        net_amount_unit: Amount unit (g, kg, ml, l, cl, dl, stk)
        pack_count: Number of items in pack (e.g., 6 for 6-pack)

    Returns:
        UnitPrice with value and unit, or None if cannot calculate
    """
    if not price_value or price_value <= 0:
        return None

    if not net_amount_value or net_amount_value <= 0:
        return None

    if not net_amount_unit:
        return None

    # Price excluding deposit
    effective_price = price_value - (deposit_value or 0)
    if effective_price <= 0:
        effective_price = price_value

    # Total amount (considering pack count)
    total_amount = net_amount_value * (pack_count or 1)

    unit = net_amount_unit.lower().strip()

    # Calculate based on unit type
    if unit in ('ml', 'milliliter'):
        # ml -> kr/L
        unit_price = effective_price / (total_amount / 1000)
        return UnitPrice(value=round(unit_price, 2), unit="kr/L")

    elif unit == 'cl':
        # cl -> kr/L (1 cl = 10 ml)
        unit_price = effective_price / (total_amount / 100)
        return UnitPrice(value=round(unit_price, 2), unit="kr/L")

    elif unit == 'dl':
        # dl -> kr/L (1 dl = 100 ml)
        unit_price = effective_price / (total_amount / 10)
        return UnitPrice(value=round(unit_price, 2), unit="kr/L")

    elif unit in ('l', 'liter'):
        # L -> kr/L
        unit_price = effective_price / total_amount
        return UnitPrice(value=round(unit_price, 2), unit="kr/L")

    elif unit in ('g', 'gram'):
        # g -> kr/kg
        unit_price = effective_price / (total_amount / 1000)
        return UnitPrice(value=round(unit_price, 2), unit="kr/kg")

    elif unit in ('kg', 'kilo', 'kilogram'):
        # kg -> kr/kg
        unit_price = effective_price / total_amount
        return UnitPrice(value=round(unit_price, 2), unit="kr/kg")

    elif unit in ('stk', 'stk.', 'pk', 'pak', 'pakke'):
        # stk -> kr/stk
        unit_price = effective_price / total_amount
        return UnitPrice(value=round(unit_price, 2), unit="kr/stk")

    # Unknown unit
    return None


def calculate_price_excl_deposit(
    price_value: Optional[float],
    deposit_value: Optional[float]
) -> Optional[float]:
    """
    Calculate price excluding deposit.

    Args:
        price_value: Total price including deposit
        deposit_value: Deposit amount

    Returns:
        Price without deposit, or original price if no deposit
    """
    if price_value is None:
        return None

    if deposit_value is None or deposit_value <= 0:
        return price_value

    result = price_value - deposit_value
    return round(result, 2) if result > 0 else price_value


def estimate_deposit(
    container_type: Optional[str],
    net_amount_value: Optional[float],
    net_amount_unit: Optional[str],
    pack_count: Optional[int] = None
) -> Optional[float]:
    """
    Estimate deposit (pant) based on container and size.

    Danish deposit rates (2024):
    - A-pant: 1.00 kr (cans and small bottles < 1L)
    - B-pant: 1.50 kr (plastic bottles 1-20L) - rarely used
    - C-pant: 3.00 kr (large glass/metal > 1L)

    Args:
        container_type: CAN, BOTTLE, etc.
        net_amount_value: Amount value
        net_amount_unit: Amount unit
        pack_count: Number of items

    Returns:
        Estimated total deposit for the pack
    """
    if not container_type:
        return None

    container = container_type.upper()

    # Only CAN and BOTTLE have deposit
    if container not in ('CAN', 'BOTTLE'):
        return None

    # Calculate per-item deposit
    per_item_deposit = 1.0  # Default A-pant

    if container == 'BOTTLE' and net_amount_value and net_amount_unit:
        # Convert to ml
        ml = net_amount_value
        unit = net_amount_unit.lower()

        if unit in ('l', 'liter'):
            ml = net_amount_value * 1000
        elif unit == 'cl':
            ml = net_amount_value * 10
        elif unit == 'dl':
            ml = net_amount_value * 100

        # Large bottles (>= 1L) have C-pant
        if ml >= 1000:
            per_item_deposit = 3.0

    # Total deposit for pack
    item_count = pack_count or 1
    return round(per_item_deposit * item_count, 2)


def normalize_amount_to_base_unit(
    value: Optional[float],
    unit: Optional[str]
) -> Tuple[Optional[float], Optional[str]]:
    """
    Normalize amount to base unit (ml or g).

    Args:
        value: Amount value
        unit: Amount unit

    Returns:
        Tuple of (normalized_value, base_unit)
    """
    if value is None or unit is None:
        return (None, None)

    unit = unit.lower().strip()

    # Volume -> ml
    if unit in ('l', 'liter'):
        return (value * 1000, 'ml')
    elif unit == 'dl':
        return (value * 100, 'ml')
    elif unit == 'cl':
        return (value * 10, 'ml')
    elif unit in ('ml', 'milliliter'):
        return (value, 'ml')

    # Weight -> g
    elif unit in ('kg', 'kilo', 'kilogram'):
        return (value * 1000, 'g')
    elif unit in ('g', 'gram'):
        return (value, 'g')

    # Count -> stk
    elif unit in ('stk', 'stk.', 'pk', 'pak', 'pakke'):
        return (value, 'stk')

    return (value, unit)
